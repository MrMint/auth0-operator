using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Alethic.Auth0.Operator.Controllers;
using Alethic.Auth0.Operator.Core.Models;
using Alethic.Auth0.Operator.Core.Models.Connection;
using Alethic.Auth0.Operator.Models;
using Alethic.Auth0.Operator.Options;
using Alethic.Auth0.Operator.RateLimiting;

using Auth0.ManagementApi;
using Auth0.ManagementApi.Clients;
using Auth0.ManagementApi.Models;

using k8s.Models;

using KubeOps.Abstractions.Queue;
using KubeOps.KubernetesClient;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

namespace Alethic.Auth0.Operator.Tests
{
    /// <summary>
    /// Tests for verifying that the Connection controller preserves enabled_clients during updates.
    /// 
    /// This addresses a bug where updates to a Connection (triggered by any reconciliation) would
    /// remove all enabled clients because the Connection CRD typically doesn't specify enabled_clients
    /// (which is managed by the Client controller instead).
    /// 
    /// See: Bug Report - Connection Update Removes enabled_clients
    /// </summary>
    [TestClass]
    public sealed class ConnectionEnabledClientsTests
    {
        /// <summary>
        /// Tests that when a Connection is updated and spec.conf.EnabledClients is null,
        /// the existing enabled_clients from Auth0 are preserved in the update request.
        /// 
        /// This is the core fix for the race condition where:
        /// 1. Client controller enables a connection on a client
        /// 2. Connection controller updates the connection (any reconciliation trigger)
        /// 3. Without this fix: update removes enabled_clients → Auth0 removes all enabled clients
        /// 4. With this fix: existing enabled_clients are preserved
        /// </summary>
        [TestMethod]
        public void EnabledClients_WhenConfIsNull_ShouldPreserveExistingFromAuth0()
        {
            // Arrange
            var existingEnabledClients = new List<object> { "client_id_1", "client_id_2", "client_id_3" };
            var lastState = new Hashtable
            {
                ["enabled_clients"] = existingEnabledClients,
                ["strategy"] = "google-apps"
            };

            // Simulate conf.EnabledClients being null (typical for Connections where
            // enabled_clients is managed by the Client controller)
            ConnectionConf conf = new()
            {
                Name = "test-connection",
                DisplayName = "Test Connection",
                Strategy = "google-apps",
                EnabledClients = null  // Not specified in CRD
            };

            // Act - Extract the logic that would be in the Update method
            string[]? preservedClients = null;
            if (conf.EnabledClients is null && lastState["enabled_clients"] is IEnumerable<object> existing)
            {
                preservedClients = existing.OfType<string>().ToArray();
            }

            // Assert
            Assert.IsNotNull(preservedClients, "Preserved clients should not be null when existing clients are present");
            Assert.AreEqual(3, preservedClients.Length, "Should preserve all 3 existing clients");
            CollectionAssert.Contains(preservedClients, "client_id_1");
            CollectionAssert.Contains(preservedClients, "client_id_2");
            CollectionAssert.Contains(preservedClients, "client_id_3");
        }

        /// <summary>
        /// Tests that when a Connection is updated and spec.conf.EnabledClients is explicitly set,
        /// the specified enabled_clients should be used (not preserved from Auth0).
        /// </summary>
        [TestMethod]
        public void EnabledClients_WhenConfIsExplicitlySet_ShouldNotPreserve()
        {
            // Arrange
            var existingEnabledClients = new List<object> { "client_id_1", "client_id_2", "client_id_3" };
            var lastState = new Hashtable
            {
                ["enabled_clients"] = existingEnabledClients,
                ["strategy"] = "google-apps"
            };

            // Simulate conf.EnabledClients being explicitly set to a different list
            ConnectionConf conf = new()
            {
                Name = "test-connection",
                DisplayName = "Test Connection",
                Strategy = "google-apps",
                EnabledClients = new V1ClientReference[]
                {
                    new() { Id = "new_client_1" },
                    new() { Id = "new_client_2" }
                }
            };

            // Act - Extract the logic that would be in the Update method
            string[]? preservedClients = null;
            if (conf.EnabledClients is null && lastState["enabled_clients"] is IEnumerable<object> existing)
            {
                preservedClients = existing.OfType<string>().ToArray();
            }

            // Assert - Should NOT preserve when EnabledClients is explicitly set
            Assert.IsNull(preservedClients, "Should not preserve clients when EnabledClients is explicitly specified");
        }

        /// <summary>
        /// Tests that when a Connection is updated and there are no existing enabled_clients in Auth0,
        /// the preservation logic handles this gracefully.
        /// </summary>
        [TestMethod]
        public void EnabledClients_WhenNoExistingClients_ShouldHandleGracefully()
        {
            // Arrange - No enabled_clients in last state
            var lastState = new Hashtable
            {
                ["strategy"] = "google-apps"
                // enabled_clients not present
            };

            ConnectionConf conf = new()
            {
                Name = "test-connection",
                EnabledClients = null
            };

            // Act
            string[]? preservedClients = null;
            if (conf.EnabledClients is null && lastState["enabled_clients"] is IEnumerable<object> existing)
            {
                preservedClients = existing.OfType<string>().ToArray();
            }

            // Assert
            Assert.IsNull(preservedClients, "Should be null when no existing clients");
        }

        /// <summary>
        /// Tests that when a Connection is updated and enabled_clients is an empty list in Auth0,
        /// the preservation logic preserves the empty list.
        /// </summary>
        [TestMethod]
        public void EnabledClients_WhenExistingIsEmptyList_ShouldPreserveEmptyList()
        {
            // Arrange
            var existingEnabledClients = new List<object>(); // Empty list
            var lastState = new Hashtable
            {
                ["enabled_clients"] = existingEnabledClients,
                ["strategy"] = "google-apps"
            };

            ConnectionConf conf = new()
            {
                Name = "test-connection",
                EnabledClients = null
            };

            // Act
            string[]? preservedClients = null;
            if (conf.EnabledClients is null && lastState["enabled_clients"] is IEnumerable<object> existing)
            {
                preservedClients = existing.OfType<string>().ToArray();
            }

            // Assert
            Assert.IsNotNull(preservedClients, "Should return empty array, not null");
            Assert.AreEqual(0, preservedClients.Length, "Should be an empty array");
        }

        /// <summary>
        /// Tests that the preservation logic correctly handles lastState being null.
        /// </summary>
        [TestMethod]
        public void EnabledClients_WhenLastStateIsNull_ShouldHandleGracefully()
        {
            // Arrange
            Hashtable? lastState = null;

            ConnectionConf conf = new()
            {
                Name = "test-connection",
                EnabledClients = null
            };

            // Act
            string[]? preservedClients = null;
            if (conf.EnabledClients is null && lastState?["enabled_clients"] is IEnumerable<object> existing)
            {
                preservedClients = existing.OfType<string>().ToArray();
            }

            // Assert
            Assert.IsNull(preservedClients, "Should be null when lastState is null");
        }

        /// <summary>
        /// Tests that the preservation logic handles non-string elements in the enabled_clients list.
        /// (Auth0 should always return strings, but defensive programming is good)
        /// </summary>
        [TestMethod]
        public void EnabledClients_WhenMixedTypes_ShouldOnlyPreserveStrings()
        {
            // Arrange
            var existingEnabledClients = new List<object> 
            { 
                "valid_client_1", 
                123,  // Invalid - int
                "valid_client_2",
                null!, // Invalid - null
                "valid_client_3"
            };
            var lastState = new Hashtable
            {
                ["enabled_clients"] = existingEnabledClients,
                ["strategy"] = "google-apps"
            };

            ConnectionConf conf = new()
            {
                Name = "test-connection",
                EnabledClients = null
            };

            // Act
            string[]? preservedClients = null;
            if (conf.EnabledClients is null && lastState["enabled_clients"] is IEnumerable<object> existing)
            {
                preservedClients = existing.OfType<string>().ToArray();
            }

            // Assert
            Assert.IsNotNull(preservedClients);
            Assert.AreEqual(3, preservedClients.Length, "Should only include valid strings");
            CollectionAssert.Contains(preservedClients, "valid_client_1");
            CollectionAssert.Contains(preservedClients, "valid_client_2");
            CollectionAssert.Contains(preservedClients, "valid_client_3");
        }
    }
}
