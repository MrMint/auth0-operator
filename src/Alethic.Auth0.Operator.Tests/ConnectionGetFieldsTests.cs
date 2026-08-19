using System;
using System.Linq;
using System.Reflection;

using Alethic.Auth0.Operator.Controllers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Alethic.Auth0.Operator.Tests
{

    /// <summary>
    /// Guards the <c>fields</c> allow-list that <see cref="V1ConnectionController"/> sends on
    /// <c>GET /api/v2/connections/{id}</c>. Auth0 validates that parameter against a fixed set and
    /// rejects the entire request with a 400 when it names anything else, which wedges every
    /// Connection reconcile. This has already happened twice, so the list is pinned here.
    /// </summary>
    [TestClass]
    public sealed class ConnectionGetFieldsTests
    {

        /// <summary>
        /// The values Auth0 accepts, verbatim from the 400 it returns for an invalid list.
        /// </summary>
        static readonly string[] AcceptedByAuth0 =
        [
            "name",
            "display_name",
            "strategy",
            "options",
            "id",
            "provisioning_ticket_url",
            "metadata",
            "show_as_button",
            "clients",
            "authentication",
            "connected_accounts",
            "cross_app_access_requesting_app",
            "cross_app_access_resource_app",
            "enabled_clients",
        ];

        static string[] GetFields()
        {
            var field =
                typeof(V1ConnectionController).GetField(
                    "GetFields",
                    BindingFlags.NonPublic | BindingFlags.Static
                )
                ?? throw new InvalidOperationException(
                    "V1ConnectionController.GetFields is missing; the connection GET allow-list is no longer pinned."
                );

            var value =
                field.GetValue(null) as string
                ?? throw new InvalidOperationException("V1ConnectionController.GetFields is not a string.");

            return value.Split(',');
        }

        [TestMethod]
        public void FieldsMustAllBeAcceptedByAuth0()
        {
            foreach (var field in GetFields())
                Assert.IsTrue(
                    AcceptedByAuth0.Contains(field),
                    $"'{field}' is not accepted by Auth0 on GET /api/v2/connections/{{id}}; including it makes Auth0 reject the whole request with a 400."
                );
        }

        [TestMethod]
        public void FieldsMustNotNameEnabledClients()
        {
            // enabled_clients is deprecated on this endpoint and naming it at all — even to exclude
            // it — trips Auth0's deprecation detection. It is read from /connections/{id}/clients.
            CollectionAssert.DoesNotContain(
                GetFields(),
                "enabled_clients",
                "enabled_clients must not be named on GET /api/v2/connections/{id}."
            );
        }

        [TestMethod]
        public void FieldsMustBeWellFormed()
        {
            var fields = GetFields();

            Assert.IsTrue(fields.Length > 0, "The allow-list must not be empty.");

            foreach (var field in fields)
                Assert.AreEqual(field.Trim(), field, $"'{field}' has surrounding whitespace; Auth0 matches the list exactly.");

            CollectionAssert.AllItemsAreUnique(fields, "The allow-list contains a duplicate field.");
        }

    }

}
