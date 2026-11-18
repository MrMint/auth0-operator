using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Alethic.Auth0.Operator.Controllers;
using Alethic.Auth0.Operator.Models;
using Alethic.Auth0.Operator.Options;
using Alethic.Auth0.Operator.RateLimiting;

using Auth0.ManagementApi;

using k8s.Models;

using KubeOps.Abstractions.Queue;
using KubeOps.KubernetesClient;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

namespace Alethic.Auth0.Operator.Tests
{

    /// <summary>
    /// Tests for the status written by <see cref="V1ConnectionController"/> for a reconciled
    /// <see cref="V1Connection"/>. The Auth0 connection options blob carries strategy-specific
    /// credentials (for example the Google OAuth <c>client_secret</c>), so it must never reach
    /// <c>status.lastConf</c>, which is readable by anyone holding get on the CRD.
    /// </summary>
    [TestClass]
    public sealed class ConnectionStatusTests
    {

        const string Namespace = "care-platform-core";

        static readonly MethodInfo ApplyStatusMethod =
            typeof(V1ConnectionController).GetMethod(
                "ApplyStatus",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            ?? throw new InvalidOperationException("Could not locate ApplyStatus via reflection.");

        static V1Connection NewConnection()
        {
            return new V1Connection
            {
                ApiVersion = "kubernetes.auth0.com/v1",
                Kind = "Connection",
                Metadata = new V1ObjectMeta(name: "chartspan-google-workspace", namespaceProperty: Namespace),
                Status = new V1Connection.StatusDef(),
            };
        }

        static V1ConnectionController NewController()
        {
            EntityRequeue<V1Connection> requeue = (_, _) => { };

            return new V1ConnectionController(
                Mock.Of<IKubernetesClient>(),
                requeue,
                new MemoryCache(new MemoryCacheOptions()),
                Mock.Of<ILogger<V1ConnectionController>>(),
                Microsoft.Extensions.Options.Options.Create(new OperatorOptions()),
                Mock.Of<IManagementApiClientFactory>(),
                Mock.Of<IRateLimiterService>(),
                Mock.Of<IReconciliationScheduler>());
        }

        /// <summary>
        /// Runs ApplyStatus over <paramref name="lastConf"/> and returns the status the
        /// controller would persist.
        /// </summary>
        static async Task<Hashtable?> RunApplyStatus(Hashtable lastConf)
        {
            var entity = NewConnection();

            await (Task)ApplyStatusMethod.Invoke(
                NewController(),
                new object?[] { null, entity, lastConf, Namespace, CancellationToken.None })!;

            return entity.Status.LastConf;
        }

        /// <summary>
        /// Models what Get() builds for a google-apps connection: the options Hashtable comes
        /// back from Auth0 verbatim, client_secret included.
        /// </summary>
        static Hashtable NewGoogleWorkspaceLastConf()
        {
            return new Hashtable
            {
                ["id"] = "con_abc123",
                ["name"] = "chartspan-google-workspace",
                ["display_name"] = "Google Workspace",
                ["strategy"] = "google-apps",
                ["enabled_clients"] = new[] { "client_one", "client_two" },
                ["options"] = new Hashtable
                {
                    ["client_id"] = "1234.apps.googleusercontent.com",
                    ["client_secret"] = "GOCSPX-super-secret-value",
                    ["tenant_domain"] = "chartspan.com",
                },
            };
        }

        [TestMethod]
        public async Task Options_are_removed_from_persisted_status()
        {
            var status = await RunApplyStatus(NewGoogleWorkspaceLastConf());

            Assert.IsNotNull(status, "Expected a status to be persisted.");
            Assert.IsFalse(
                status!.ContainsKey("options"),
                "options must not reach status.lastConf: it carries the connection client_secret.");
        }

        [TestMethod]
        public async Task Non_sensitive_status_keys_are_preserved()
        {
            var status = await RunApplyStatus(NewGoogleWorkspaceLastConf());

            Assert.IsNotNull(status);
            Assert.AreEqual("con_abc123", status!["id"]);
            Assert.AreEqual("chartspan-google-workspace", status["name"]);
            Assert.AreEqual("Google Workspace", status["display_name"]);
            Assert.AreEqual("google-apps", status["strategy"], "strategy is read by Update() and must survive.");
            CollectionAssert.AreEqual(
                new[] { "client_one", "client_two" },
                (string[]?)status["enabled_clients"],
                "enabled_clients is read by Update() and must survive.");
        }

        [TestMethod]
        public async Task Missing_options_key_is_tolerated()
        {
            // Strategies whose Get() response carries no options at all must not fault.
            var lastConf = new Hashtable
            {
                ["id"] = "con_abc123",
                ["strategy"] = "auth0",
            };

            var status = await RunApplyStatus(lastConf);

            Assert.IsNotNull(status);
            Assert.AreEqual("auth0", status!["strategy"]);
            Assert.IsFalse(status.ContainsKey("options"));
        }

    }

}
