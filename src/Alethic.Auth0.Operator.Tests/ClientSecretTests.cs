using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Alethic.Auth0.Operator.Controllers;
using Alethic.Auth0.Operator.Models;
using Alethic.Auth0.Operator.Options;
using Alethic.Auth0.Operator.RateLimiting;

using k8s.Models;

using KubeOps.Abstractions.Entities;
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
    /// Tests for the credentials Secret that <see cref="V1ClientController"/> writes for a
    /// reconciled <see cref="V1Client"/>. The Secret must carry both the historical lowercase
    /// <c>clientId</c> key and the capitalized <c>clientID</c> key (kept identical) so that
    /// consumers requiring the AWS Load Balancer Controller casing work without breaking
    /// existing consumers.
    /// </summary>
    [TestClass]
    public sealed class ClientSecretTests
    {

        const string Namespace = "default";
        const string SecretName = "my-secret";
        const string ClientUid = "client-uid-123";

        static readonly MethodInfo ApplySecretMethod =
            typeof(V1ClientController).GetMethod("ApplySecret", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not locate ApplySecret via reflection.");

        static V1Client NewClient()
        {
            return new V1Client
            {
                ApiVersion = "kubernetes.auth0.com/v1",
                Kind = "Client",
                Metadata = new V1ObjectMeta(name: "my-client", namespaceProperty: Namespace, uid: ClientUid),
                Spec = new V1Client.SpecDef
                {
                    SecretRef = new V1SecretReference { Name = SecretName, NamespaceProperty = Namespace },
                },
            };
        }

        /// <summary>
        /// Builds the controller wired to <paramref name="kube"/>, with every other dependency mocked.
        /// </summary>
        static V1ClientController NewController(IKubernetesClient kube)
        {
            EntityRequeue<V1Client> requeue = (_, _) => { };

            return new V1ClientController(
                kube,
                requeue,
                new MemoryCache(new MemoryCacheOptions()),
                Mock.Of<ILogger<V1ClientController>>(),
                Microsoft.Extensions.Options.Options.Create(new OperatorOptions()),
                Mock.Of<IManagementApiClientFactory>(),
                Mock.Of<IRateLimiterService>(),
                Mock.Of<IReconciliationScheduler>());
        }

        /// <summary>
        /// Runs ApplySecret against a mocked Kubernetes client and returns the Secret that was
        /// ultimately persisted via UpdateAsync. <paramref name="existing"/> is what GetAsync
        /// resolves to (null models a Secret that does not yet exist).
        /// </summary>
        static async Task<V1Secret> RunApplySecret(string? clientId, string? clientSecret, V1Secret? existing)
        {
            var kube = new Mock<IKubernetesClient>(MockBehavior.Strict);

            kube
                .Setup(k => k.GetAsync<V1Secret>(SecretName, Namespace, It.IsAny<CancellationToken>()))
                .ReturnsAsync(existing);

            kube
                .Setup(k => k.CreateAsync(It.IsAny<V1Secret>(), It.IsAny<CancellationToken>()))
                .Returns((V1Secret s, CancellationToken _) => Task.FromResult(s));

            V1Secret? persisted = null;
            kube
                .Setup(k => k.UpdateAsync(It.IsAny<V1Secret>(), It.IsAny<CancellationToken>()))
                .Returns((V1Secret s, CancellationToken _) => { persisted = s; return Task.FromResult(s); });

            var controller = NewController(kube.Object);
            var entity = NewClient();

            await (Task)ApplySecretMethod.Invoke(
                controller,
                new object?[] { entity, clientId, clientSecret, Namespace, CancellationToken.None })!;

            Assert.IsNotNull(persisted, "Expected the Secret to be persisted via UpdateAsync.");
            Assert.IsNotNull(persisted!.StringData, "Expected the persisted Secret to carry StringData.");
            return persisted;
        }

        [TestMethod]
        public async Task Fresh_secret_contains_clientId_clientID_and_clientSecret()
        {
            // GetAsync returns null -> the controller creates the Secret fresh.
            var secret = await RunApplySecret(clientId: "abc123", clientSecret: "s3cr3t", existing: null);

            var data = secret.StringData;
            Assert.IsTrue(data.ContainsKey("clientId"), "clientId key must be present.");
            Assert.IsTrue(data.ContainsKey("clientID"), "clientID key must be present.");
            Assert.IsTrue(data.ContainsKey("clientSecret"), "clientSecret key must be present.");

            Assert.AreEqual("abc123", data["clientId"]);
            Assert.AreEqual("s3cr3t", data["clientSecret"]);
            Assert.AreEqual(data["clientId"], data["clientID"], "clientID must equal clientId.");
        }

        [TestMethod]
        public async Task Existing_owned_secret_is_backfilled_with_clientID()
        {
            // Simulate a Secret created before the clientID key existed: it is owned by the
            // client and already carries the lowercase keys, but has no clientID.
            var existing = new V1Secret(
                    metadata: new V1ObjectMeta(name: SecretName, namespaceProperty: Namespace))
                .WithOwnerReference(NewClient());
            existing.StringData = new Dictionary<string, string>
            {
                ["clientId"] = "abc123",
                ["clientSecret"] = "s3cr3t",
            };

            var secret = await RunApplySecret(clientId: "abc123", clientSecret: "s3cr3t", existing: existing);

            Assert.IsTrue(secret.StringData.ContainsKey("clientID"), "clientID must be backfilled onto existing secrets.");
            Assert.AreEqual("abc123", secret.StringData["clientId"]);
            Assert.AreEqual(secret.StringData["clientId"], secret.StringData["clientID"], "clientID must equal clientId.");
        }

        [TestMethod]
        public async Task Rotating_client_secret_updates_clientSecret_and_keeps_clientID_in_sync()
        {
            var existing = new V1Secret(
                    metadata: new V1ObjectMeta(name: SecretName, namespaceProperty: Namespace))
                .WithOwnerReference(NewClient());
            existing.StringData = new Dictionary<string, string>
            {
                ["clientId"] = "abc123",
                ["clientID"] = "abc123",
                ["clientSecret"] = "old-secret",
            };

            // Rotation: same clientId, new clientSecret value.
            var secret = await RunApplySecret(clientId: "abc123", clientSecret: "new-secret", existing: existing);

            Assert.AreEqual("new-secret", secret.StringData["clientSecret"], "clientSecret must be rotated.");
            Assert.AreEqual("abc123", secret.StringData["clientId"]);
            Assert.AreEqual(secret.StringData["clientId"], secret.StringData["clientID"], "clientID must remain equal to clientId after rotation.");
        }

    }

}
