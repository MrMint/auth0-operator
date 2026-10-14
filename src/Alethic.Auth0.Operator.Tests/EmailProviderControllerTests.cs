using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Alethic.Auth0.Operator.Controllers;
using Alethic.Auth0.Operator.Core.Models;
using Alethic.Auth0.Operator.Core.Models.EmailProvider;
using Alethic.Auth0.Operator.Models;
using Alethic.Auth0.Operator.Options;
using Alethic.Auth0.Operator.RateLimiting;

using Auth0.Core.Exceptions;
using Auth0.ManagementApi;
using Auth0.ManagementApi.Clients;
using Auth0.ManagementApi.Models;

using k8s.Models;

using KubeOps.Abstractions.Queue;
using KubeOps.KubernetesClient;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using Newtonsoft.Json.Linq;

namespace Alethic.Auth0.Operator.Tests
{

    /// <summary>
    /// Tests for <see cref="V1EmailProviderController"/>, which manages the tenant email provider singleton.
    /// Each test drives the public <c>ReconcileAsync</c> entry point against mocked Kubernetes and Auth0 clients.
    /// </summary>
    [TestClass]
    public sealed class EmailProviderControllerTests
    {

        const string Namespace = "example";
        const string TenantName = "example-tenant";
        const string SecretName = "email-provider-credentials";
        const string SecretAccessKeyValue = "example-secret-access-key";
        const string SmtpPassValue = "example-smtp-password";

        /// <summary>
        /// Holds the mocks and everything the controller wrote during a reconcile.
        /// </summary>
        sealed class Harness
        {

            public Mock<IKubernetesClient> Kube { get; } = new();

            public Mock<IEmailProviderClient> Emails { get; } = new(MockBehavior.Strict);

            public List<V1EmailProvider> EmailProviders { get; } = new();

            public Dictionary<string, V1Secret> Secrets { get; } = new();

            public List<Eventsv1Event> Events { get; } = new();

            public List<TimeSpan> Requeues { get; } = new();

            public List<string> StatusWrites { get; } = new();

            public EmailProviderConfigureRequest? Configured { get; set; }

            public EmailProviderUpdateRequest? Updated { get; set; }

            public V1EmailProviderController Controller { get; }

            public Harness()
            {
                var tenant = new V1Tenant
                {
                    ApiVersion = "kubernetes.auth0.com/v1",
                    Kind = "Tenant",
                    Metadata = new V1ObjectMeta(name: TenantName, namespaceProperty: Namespace, uid: "tenant-uid"),
                };

                Kube
                    .Setup(k => k.GetAsync<V1Tenant>(TenantName, Namespace, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(tenant);
                Kube
                    .Setup(k => k.GetAsync<V1Secret>(It.IsAny<string>(), Namespace, It.IsAny<CancellationToken>()))
                    .Returns((string name, string? _, CancellationToken _) =>
                        Task.FromResult(Secrets.TryGetValue(name, out var s) ? s : null));
                Kube
                    .Setup(k => k.ListAsync<V1EmailProvider>(null, null, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(() => EmailProviders);
                Kube
                    .Setup(k => k.UpdateStatusAsync(It.IsAny<V1EmailProvider>(), It.IsAny<CancellationToken>()))
                    .Returns((V1EmailProvider e, CancellationToken _) =>
                    {
                        StatusWrites.Add(System.Text.Json.JsonSerializer.Serialize(e.Status));
                        return Task.FromResult(e);
                    });
                Kube
                    .Setup(k => k.CreateAsync(It.IsAny<Eventsv1Event>(), It.IsAny<CancellationToken>()))
                    .Returns((Eventsv1Event e, CancellationToken _) =>
                    {
                        Events.Add(e);
                        return Task.FromResult(e);
                    });

                var api = new Mock<IManagementApiClient>();
                api.SetupGet(a => a.EmailProvider).Returns(Emails.Object);

                // pre-seed the tenant API context so no token request is made
                var cache = new MemoryCache(new MemoryCacheOptions());
                cache.Set(
                    (Namespace, TenantName),
                    new TenantApiContext(api.Object, "token", new Uri("https://example.com/api/v2/")));

                var scheduler = new Mock<IReconciliationScheduler>();
                scheduler
                    .Setup(s => s.BeginReconcileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(SchedulingDecision.Proceed());

                EntityRequeue<V1EmailProvider> requeue = (_, delay) => Requeues.Add(delay);

                Controller = new V1EmailProviderController(
                    Kube.Object,
                    requeue,
                    cache,
                    Mock.Of<ILogger<V1EmailProviderController>>(),
                    Microsoft.Extensions.Options.Options.Create(new OperatorOptions()),
                    Mock.Of<IManagementApiClientFactory>(),
                    Mock.Of<IRateLimiterService>(),
                    scheduler.Object);
            }

            public void AddSecret(string name, IDictionary<string, string> data)
            {
                Secrets[name] = new V1Secret
                {
                    Metadata = new V1ObjectMeta(name: name, namespaceProperty: Namespace),
                    Data = data.ToDictionary(kv => kv.Key, kv => Encoding.UTF8.GetBytes(kv.Value)),
                };
            }

            /// <summary>
            /// Auth0 has no email provider configured.
            /// </summary>
            public void ProviderMissing()
            {
                Emails
                    .Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new ErrorApiException(HttpStatusCode.NotFound));
            }

            /// <summary>
            /// Auth0 has an email provider configured. The response includes credentials, as a defensive check
            /// that they are stripped even if Auth0 ever returned them.
            /// </summary>
            public void ProviderExists()
            {
                Emails
                    .Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new EmailProvider
                    {
                        Name = "ses",
                        IsEnabled = true,
                        DefaultFromAddress = "no-reply@example.com",
                        Credentials = new EmailProviderCredentials
                        {
                            AccessKeyId = "EXAMPLEACCESSKEYID",
                            SecretAccessKey = SecretAccessKeyValue,
                            Region = "us-east-1",
                        },
                        Settings = JObject.Parse("{\"message\":{\"configuration_set_name\":\"example\"}}"),
                    });
            }

            public void AllowConfigure()
            {
                Emails
                    .Setup(c => c.ConfigureAsync(It.IsAny<EmailProviderConfigureRequest>(), It.IsAny<CancellationToken>()))
                    .Returns((EmailProviderConfigureRequest r, CancellationToken _) =>
                    {
                        Configured = r;
                        // once configured, subsequent GETs succeed
                        ProviderExists();
                        return Task.FromResult(new EmailProvider { Name = r.Name });
                    });
            }

            public void AllowUpdate()
            {
                Emails
                    .Setup(c => c.UpdateAsync(It.IsAny<EmailProviderUpdateRequest>(), It.IsAny<CancellationToken>()))
                    .Returns((EmailProviderUpdateRequest r, CancellationToken _) =>
                    {
                        Updated = r;
                        return Task.FromResult(new EmailProvider { Name = r.Name });
                    });
            }

            public Task ReconcileAsync(V1EmailProvider entity) =>
                Controller.ReconcileAsync(entity, CancellationToken.None);

        }

        static V1EmailProvider NewEmailProvider(
            string name = "email-provider",
            EmailProviderCredentialsConf? credentials = null,
            DateTime? created = null,
            string? uid = null)
        {
            return new V1EmailProvider
            {
                ApiVersion = "kubernetes.auth0.com/v1",
                Kind = "EmailProvider",
                Metadata = new V1ObjectMeta(
                    name: name,
                    namespaceProperty: Namespace,
                    uid: uid ?? $"{name}-uid",
                    creationTimestamp: created ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                Spec = new V1EmailProvider.SpecDef
                {
                    TenantRef = new V1TenantReference { Name = TenantName },
                    Conf = new EmailProviderConf
                    {
                        Name = "ses",
                        Enabled = true,
                        DefaultFromAddress = "no-reply@example.com",
                        Credentials = credentials ?? SesCredentialsFromSecret(),
                    },
                },
                Status = new V1EmailProvider.StatusDef(),
            };
        }

        static EmailProviderCredentialsConf SesCredentialsFromSecret() => new()
        {
            AccessKeyId = new EmailProviderCredentialValue { Value = "EXAMPLEACCESSKEYID" },
            SecretAccessKey = new EmailProviderCredentialValue
            {
                SecretKeyRef = new SecretKeySelector { Name = SecretName, Key = "secretAccessKey" },
            },
            Region = new EmailProviderCredentialValue { Value = "us-east-1" },
        };

        static Harness NewHarnessWithSecret()
        {
            var h = new Harness();
            h.AddSecret(SecretName, new Dictionary<string, string>
            {
                ["secretAccessKey"] = SecretAccessKeyValue,
                ["smtp_pass"] = SmtpPassValue,
            });
            return h;
        }

        static void AssertNoAuth0Writes(Harness h)
        {
            h.Emails.Verify(c => c.ConfigureAsync(It.IsAny<EmailProviderConfigureRequest>(), It.IsAny<CancellationToken>()), Times.Never());
            h.Emails.Verify(c => c.UpdateAsync(It.IsAny<EmailProviderUpdateRequest>(), It.IsAny<CancellationToken>()), Times.Never());
            h.Emails.Verify(c => c.DeleteAsync(It.IsAny<CancellationToken>()), Times.Never());
        }

        [TestMethod]
        public async Task Find_adopts_existing_provider_and_updates_it()
        {
            var h = NewHarnessWithSecret();
            h.ProviderExists();
            h.AllowUpdate();
            var entity = NewEmailProvider();
            h.EmailProviders.Add(entity);

            await h.ReconcileAsync(entity);

            Assert.AreEqual(V1EmailProviderController.ProviderId, entity.Status.Id);
            h.Emails.Verify(c => c.ConfigureAsync(It.IsAny<EmailProviderConfigureRequest>(), It.IsAny<CancellationToken>()), Times.Never());
            Assert.IsNotNull(h.Updated, "Expected the existing provider to be updated.");
        }

        [TestMethod]
        public async Task Get_requests_only_non_secret_fields()
        {
            var h = NewHarnessWithSecret();
            h.ProviderExists();
            h.AllowUpdate();
            var entity = NewEmailProvider();
            h.EmailProviders.Add(entity);

            await h.ReconcileAsync(entity);

            h.Emails.Verify(c => c.GetAsync("name,enabled,default_from_address,settings", true, It.IsAny<CancellationToken>()), Times.AtLeastOnce());
            h.Emails.Verify(c => c.GetAsync(It.Is<string>(f => f.Contains("credentials")), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never());
        }

        [TestMethod]
        public async Task Create_configures_provider_when_none_exists()
        {
            var h = NewHarnessWithSecret();
            h.ProviderMissing();
            h.AllowConfigure();
            h.AllowUpdate();
            var entity = NewEmailProvider();
            h.EmailProviders.Add(entity);

            await h.ReconcileAsync(entity);

            Assert.IsNotNull(h.Configured, "Expected the provider to be configured via POST.");
            Assert.AreEqual("ses", h.Configured!.Name);
            Assert.AreEqual(true, h.Configured.IsEnabled);
            Assert.AreEqual("no-reply@example.com", h.Configured.DefaultFromAddress);
            Assert.AreEqual(SecretAccessKeyValue, h.Configured.Credentials.SecretAccessKey);
            Assert.AreEqual(V1EmailProviderController.ProviderId, entity.Status.Id);
        }

        [TestMethod]
        public async Task Update_always_sends_resolved_credentials()
        {
            var h = NewHarnessWithSecret();
            h.ProviderExists();
            h.AllowUpdate();
            var entity = NewEmailProvider();
            entity.Status.Id = V1EmailProviderController.ProviderId;
            h.EmailProviders.Add(entity);

            await h.ReconcileAsync(entity);

            Assert.IsNotNull(h.Updated);
            Assert.AreEqual("EXAMPLEACCESSKEYID", h.Updated!.Credentials.AccessKeyId);
            Assert.AreEqual(SecretAccessKeyValue, h.Updated.Credentials.SecretAccessKey);
            Assert.AreEqual("us-east-1", h.Updated.Credentials.Region);
        }

        [TestMethod]
        public async Task Update_picks_up_rotated_secret()
        {
            var h = NewHarnessWithSecret();
            h.ProviderExists();
            h.AllowUpdate();
            var entity = NewEmailProvider();
            entity.Status.Id = V1EmailProviderController.ProviderId;
            h.EmailProviders.Add(entity);

            await h.ReconcileAsync(entity);
            h.AddSecret(SecretName, new Dictionary<string, string> { ["secretAccessKey"] = "rotated-value" });
            await h.ReconcileAsync(entity);

            Assert.AreEqual("rotated-value", h.Updated!.Credentials.SecretAccessKey);
        }

        [TestMethod]
        public async Task Literal_and_secretKeyRef_credentials_both_resolve()
        {
            var h = NewHarnessWithSecret();
            h.ProviderExists();
            h.AllowUpdate();
            var entity = NewEmailProvider(credentials: new EmailProviderCredentialsConf
            {
                SmtpHost = new EmailProviderCredentialValue { Value = "smtp.example.com" },
                SmtpPort = new EmailProviderCredentialValue { Value = "587" },
                SmtpUser = new EmailProviderCredentialValue { Value = "example-user" },
                SmtpPass = new EmailProviderCredentialValue
                {
                    SecretKeyRef = new SecretKeySelector { Name = SecretName, Key = "smtp_pass" },
                },
            });
            entity.Spec.Conf!.Name = "smtp";
            h.EmailProviders.Add(entity);

            await h.ReconcileAsync(entity);

            var c = h.Updated!.Credentials;
            Assert.AreEqual("smtp.example.com", c.SmtpHost);
            Assert.AreEqual(587, c.SmtpPort);
            Assert.AreEqual("example-user", c.SmtpUsername);
            Assert.AreEqual(SmtpPassValue, c.SmtpPassword);
        }

        [TestMethod]
        public async Task Credentials_missing_from_sdk_model_are_sent()
        {
            var h = NewHarnessWithSecret();
            h.ProviderExists();
            h.AllowUpdate();
            var entity = NewEmailProvider(credentials: new EmailProviderCredentialsConf
            {
                ApiKey = new EmailProviderCredentialValue { Value = "example-api-key" },
                Domain = new EmailProviderCredentialValue { Value = "mail.example.com" },
                Region = new EmailProviderCredentialValue { Value = "eu" },
            });
            entity.Spec.Conf!.Name = "mailgun";
            h.EmailProviders.Add(entity);

            await h.ReconcileAsync(entity);

            // serialize the way the Auth0 SDK does to check the wire format
            var json = JObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(
                h.Updated,
                new Newtonsoft.Json.JsonSerializerSettings { NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore }));
            var credentials = (JObject)json["credentials"]!;
            Assert.AreEqual("example-api-key", (string?)credentials["api_key"]);
            Assert.AreEqual("mail.example.com", (string?)credentials["domain"]);
            Assert.AreEqual("eu", (string?)credentials["region"]);
            Assert.AreEqual(3, credentials.Count, "Unset credential fields must not be sent.");
        }

        [TestMethod]
        public async Task Missing_secret_retries_and_makes_no_api_call()
        {
            var h = new Harness(); // no Secrets
            var entity = NewEmailProvider();
            h.EmailProviders.Add(entity);

            await h.ReconcileAsync(entity);

            h.Emails.VerifyNoOtherCalls();
            Assert.AreEqual(1, h.Requeues.Count, "Expected the reconcile to be requeued.");
            Assert.AreEqual(TimeSpan.FromMinutes(1), h.Requeues[0]);
            Assert.IsTrue(h.Events.Any(e => e.Reason == "Retry"), "Expected a Retry event.");
            Assert.IsNull(entity.Status.Id);
        }

        [TestMethod]
        public async Task Missing_secret_key_retries_and_makes_no_api_call()
        {
            var h = new Harness();
            h.AddSecret(SecretName, new Dictionary<string, string> { ["other"] = "value" });
            var entity = NewEmailProvider();
            h.EmailProviders.Add(entity);

            await h.ReconcileAsync(entity);

            h.Emails.VerifyNoOtherCalls();
            Assert.AreEqual(1, h.Requeues.Count);
            Assert.IsTrue(h.Events.Any(e => e.Reason == "Retry"));
        }

        [TestMethod]
        public async Task Empty_secret_value_retries_and_makes_no_api_call()
        {
            var h = new Harness();
            h.AddSecret(SecretName, new Dictionary<string, string> { ["secretAccessKey"] = "" });
            var entity = NewEmailProvider();
            h.EmailProviders.Add(entity);

            await h.ReconcileAsync(entity);

            h.Emails.VerifyNoOtherCalls();
            Assert.AreEqual(1, h.Requeues.Count);
        }

        [TestMethod]
        public async Task Missing_secret_in_init_retries_and_makes_no_api_call()
        {
            var h = NewHarnessWithSecret();
            var entity = NewEmailProvider();
            entity.Spec.Init = new EmailProviderConf
            {
                Name = "ses",
                Credentials = new EmailProviderCredentialsConf
                {
                    SecretAccessKey = new EmailProviderCredentialValue
                    {
                        SecretKeyRef = new SecretKeySelector { Name = "does-not-exist", Key = "secretAccessKey" },
                    },
                },
            };
            h.EmailProviders.Add(entity);

            await h.ReconcileAsync(entity);

            h.Emails.VerifyNoOtherCalls();
            Assert.AreEqual(1, h.Requeues.Count);
        }

        [TestMethod]
        public async Task Retry_event_does_not_contain_secret_values()
        {
            var h = new Harness();
            h.AddSecret(SecretName, new Dictionary<string, string> { ["secretAccessKey"] = "   " });
            var entity = NewEmailProvider(credentials: new EmailProviderCredentialsConf
            {
                AccessKeyId = new EmailProviderCredentialValue { Value = "LITERAL-VALUE-MARKER" },
                SecretAccessKey = new EmailProviderCredentialValue
                {
                    SecretKeyRef = new SecretKeySelector { Name = SecretName, Key = "secretAccessKey" },
                },
            });
            h.EmailProviders.Add(entity);

            await h.ReconcileAsync(entity);

            foreach (var e in h.Events)
                Assert.IsFalse((e.Note ?? "").Contains("LITERAL-VALUE-MARKER"), "Event note must not contain credential values.");
        }

        [TestMethod]
        public async Task Status_never_contains_credentials()
        {
            var h = NewHarnessWithSecret();
            h.ProviderMissing();
            h.AllowConfigure();
            h.AllowUpdate();
            var entity = NewEmailProvider();
            h.EmailProviders.Add(entity);

            await h.ReconcileAsync(entity);

            Assert.IsTrue(h.StatusWrites.Count > 0, "Expected status to be written.");
            foreach (var status in h.StatusWrites)
            {
                Assert.IsFalse(status.Contains("credentials"), $"Status must not contain credentials: {status}");
                Assert.IsFalse(status.Contains(SecretAccessKeyValue), "Status must not contain a secret value.");
                Assert.IsFalse(status.Contains("EXAMPLEACCESSKEYID"), "Status must not contain a credential value.");
            }

            Assert.IsNotNull(entity.Status.LastConf);
            Assert.AreEqual("ses", entity.Status.LastConf!["name"]);
            Assert.IsFalse(entity.Status.LastConf.ContainsKey("credentials"));
        }

        [TestMethod]
        public async Task Duplicate_for_same_tenant_is_rejected_without_api_calls()
        {
            var h = NewHarnessWithSecret();
            var first = NewEmailProvider("first", created: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var second = NewEmailProvider("second", created: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
            h.EmailProviders.Add(first);
            h.EmailProviders.Add(second);

            await h.ReconcileAsync(second);

            h.Emails.VerifyNoOtherCalls();
            h.Kube.Verify(k => k.UpdateStatusAsync(It.IsAny<V1EmailProvider>(), It.IsAny<CancellationToken>()), Times.Never());
            Assert.IsNull(second.Status.Id);

            var warning = h.Events.SingleOrDefault(e => e.Reason == "DuplicateEmailProvider");
            Assert.IsNotNull(warning, "Expected a DuplicateEmailProvider warning event.");
            Assert.AreEqual("Warning", warning!.Type);
            StringAssert.Contains(warning.Note, $"{Namespace}/first");
            Assert.IsFalse(h.Events.Any(e => e.Reason == "Success"), "A rejected duplicate must not record success.");

            // keeps checking so it can take over if the owner is removed
            Assert.AreEqual(1, h.Requeues.Count);
        }

        [TestMethod]
        public async Task Owner_still_reconciles_when_a_duplicate_exists()
        {
            var h = NewHarnessWithSecret();
            h.ProviderExists();
            h.AllowUpdate();
            var first = NewEmailProvider("first", created: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var second = NewEmailProvider("second", created: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
            h.EmailProviders.Add(first);
            h.EmailProviders.Add(second);

            await h.ReconcileAsync(first);

            Assert.IsNotNull(h.Updated);
            Assert.IsFalse(h.Events.Any(e => e.Reason == "DuplicateEmailProvider"));
        }

        [TestMethod]
        public void SelectOwner_ignores_other_tenants_and_resources_being_deleted()
        {
            var self = NewEmailProvider("self", created: new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

            var otherTenant = NewEmailProvider("other-tenant", created: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            otherTenant.Spec.TenantRef = new V1TenantReference { Name = "another-tenant" };

            var deleting = NewEmailProvider("deleting", created: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            deleting.Metadata.DeletionTimestamp = DateTime.UtcNow;

            var owner = V1EmailProviderController.SelectOwner(new[] { otherTenant, deleting, self }, self);

            Assert.AreSame(self, owner);
        }

        [TestMethod]
        public void SelectOwner_treats_explicit_tenant_namespace_as_same_tenant()
        {
            var older = NewEmailProvider("older", created: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            older.Spec.TenantRef = new V1TenantReference { Name = TenantName, Namespace = Namespace };
            var self = NewEmailProvider("self", created: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

            var owner = V1EmailProviderController.SelectOwner(new[] { older, self }, self);

            Assert.AreSame(older, owner);
        }

        [TestMethod]
        public void SelectOwner_breaks_creation_time_ties_by_name()
        {
            var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var a = NewEmailProvider("a", created: created);
            var b = NewEmailProvider("b", created: created);

            Assert.AreSame(a, V1EmailProviderController.SelectOwner(new[] { a, b }, b));
            Assert.AreSame(a, V1EmailProviderController.SelectOwner(new[] { b, a }, a));
        }

        [TestMethod]
        public async Task Deleting_with_default_policy_leaves_provider_in_auth0()
        {
            var h = NewHarnessWithSecret();
            h.ProviderExists();
            var entity = NewEmailProvider();
            entity.Status.Id = V1EmailProviderController.ProviderId;

            await h.Controller.DeletedAsync(entity, CancellationToken.None);

            h.Emails.Verify(c => c.DeleteAsync(It.IsAny<CancellationToken>()), Times.Never());
        }

        [TestMethod]
        public async Task Deleting_with_delete_policy_removes_provider()
        {
            var h = NewHarnessWithSecret();
            h.ProviderExists();
            h.Emails.Setup(c => c.DeleteAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            var entity = NewEmailProvider();
            entity.Spec.Policy = [V1EntityPolicyType.Create, V1EntityPolicyType.Update, V1EntityPolicyType.Delete];
            entity.Status.Id = V1EmailProviderController.ProviderId;

            await h.Controller.DeletedAsync(entity, CancellationToken.None);

            h.Emails.Verify(c => c.DeleteAsync(It.IsAny<CancellationToken>()), Times.Once());
        }

        [TestMethod]
        public void Default_policy_does_not_include_delete()
        {
            var entity = NewEmailProvider();

            Assert.IsTrue(((V1Entity<V1EmailProvider.SpecDef, V1EmailProvider.StatusDef, EmailProviderConf>)entity).HasPolicy(V1EntityPolicyType.Create));
            Assert.IsTrue(((V1Entity<V1EmailProvider.SpecDef, V1EmailProvider.StatusDef, EmailProviderConf>)entity).HasPolicy(V1EntityPolicyType.Update));
            Assert.IsFalse(((V1Entity<V1EmailProvider.SpecDef, V1EmailProvider.StatusDef, EmailProviderConf>)entity).HasPolicy(V1EntityPolicyType.Delete));
        }

    }

}
