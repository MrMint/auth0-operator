using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Alethic.Auth0.Operator.Core.Models;
using Alethic.Auth0.Operator.Core.Models.EmailProvider;
using Alethic.Auth0.Operator.Models;
using Alethic.Auth0.Operator.Options;
using Alethic.Auth0.Operator.RateLimiting;
using Auth0.Core.Exceptions;
using Auth0.ManagementApi;
using Auth0.ManagementApi.Models;
using k8s.Models;
using KubeOps.Abstractions.Controller;
using KubeOps.Abstractions.Queue;
using KubeOps.Abstractions.Rbac;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Alethic.Auth0.Operator.Controllers
{
    /// <summary>
    /// Controller for the Auth0 tenant email provider (<c>/api/v2/emails/provider</c>).
    /// The email provider is a per-tenant singleton without an Auth0 ID, so a fixed synthetic ID is used
    /// and only the oldest EmailProvider resource targeting a tenant is reconciled.
    /// </summary>
    [EntityRbac(typeof(V1Tenant), Verbs = RbacVerb.List | RbacVerb.Get)]
    [EntityRbac(typeof(V1EmailProvider), Verbs = RbacVerb.All)]
    [EntityRbac(typeof(V1Secret), Verbs = RbacVerb.List | RbacVerb.Get)]
    [EntityRbac(typeof(Eventsv1Event), Verbs = RbacVerb.All)]
    public class V1EmailProviderController
        : V1TenantEntityController<
            V1EmailProvider,
            V1EmailProvider.SpecDef,
            V1EmailProvider.StatusDef,
            EmailProviderConf
        >,
            IEntityController<V1EmailProvider>
    {
        /// <summary>
        /// Synthetic ID stored in status once the tenant has an email provider.
        /// </summary>
        public const string ProviderId = "email-provider";

        /// <summary>
        /// Fields requested from Auth0. Limited to non-secret fields so credentials are never read back.
        /// </summary>
        public const string GetFields = "name,enabled,default_from_address,settings";

        readonly IOptions<OperatorOptions> _options;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="kube"></param>
        /// <param name="requeue"></param>
        /// <param name="cache"></param>
        /// <param name="logger"></param>
        /// <param name="options"></param>
        /// <param name="clientFactory"></param>
        /// <param name="rateLimiterService"></param>
        /// <param name="reconciliationScheduler"></param>
        public V1EmailProviderController(
            IKubernetesClient kube,
            EntityRequeue<V1EmailProvider> requeue,
            IMemoryCache cache,
            ILogger<V1EmailProviderController> logger,
            IOptions<OperatorOptions> options,
            IManagementApiClientFactory clientFactory,
            IRateLimiterService rateLimiterService,
            IReconciliationScheduler reconciliationScheduler
        )
            : base(kube, requeue, cache, logger, options, clientFactory, rateLimiterService, reconciliationScheduler)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <inheritdoc />
        protected override string EntityTypeName => "EmailProvider";

        /// <inheritdoc />
        protected override async Task<bool> Reconcile(
            V1EmailProvider entity,
            CancellationToken cancellationToken
        )
        {
            // only one resource may own a tenant's email provider; the others make no Auth0 calls
            if (entity.Spec.TenantRef is not null)
            {
                var all = await Kube.ListAsync<V1EmailProvider>(
                    @namespace: null,
                    cancellationToken: cancellationToken
                );
                var owner = SelectOwner(all, entity);
                if (IsSameResource(owner, entity) == false)
                {
                    Logger.LogWarning(
                        "{EntityTypeName} {Namespace}/{Name} ignored: tenant {Tenant} is already managed by {OwnerNamespace}/{OwnerName}.",
                        EntityTypeName,
                        entity.Namespace(),
                        entity.Name(),
                        GetTenantKey(entity),
                        owner.Namespace(),
                        owner.Name()
                    );
                    await ReconcileWarningAsync(
                        entity,
                        "DuplicateEmailProvider",
                        $"Tenant {GetTenantKey(entity)} already has its email provider managed by EmailProvider {owner.Namespace()}/{owner.Name()}. This resource is ignored until that one is removed.",
                        cancellationToken
                    );

                    // check again later so this resource takes over if the owner is removed
                    Requeue(entity, _options.Value.Reconciliation.Interval);
                    return false;
                }
            }

            // resolve every referenced credential up front so a missing Secret makes no Auth0 calls
            if (entity.Spec.Init is { } init)
                await ResolveCredentialsAsync(init.Credentials, entity.Namespace(), cancellationToken);
            if (entity.Spec.Conf is { } conf)
                await ResolveCredentialsAsync(conf.Credentials, entity.Namespace(), cancellationToken);

            return await base.Reconcile(entity, cancellationToken);
        }

        /// <summary>
        /// Gets the tenant targeted by the resource as <c>namespace/name</c>.
        /// </summary>
        public static string GetTenantKey(V1EmailProvider entity)
        {
            var tenantRef = entity.Spec.TenantRef;
            return $"{tenantRef?.Namespace ?? entity.Namespace()}/{tenantRef?.Name}";
        }

        /// <summary>
        /// Selects the resource that owns the email provider of the tenant targeted by <paramref name="self"/>:
        /// the oldest resource targeting the same tenant that is not being deleted, ties broken by namespace and name.
        /// </summary>
        public static V1EmailProvider SelectOwner(
            IEnumerable<V1EmailProvider> all,
            V1EmailProvider self
        )
        {
            var tenantKey = GetTenantKey(self);

            var candidates = all
                .Where(i => i.Spec?.TenantRef is not null)
                .Where(i => i.Metadata?.DeletionTimestamp is null)
                .Where(i => GetTenantKey(i) == tenantKey)
                .Where(i => IsSameResource(i, self) == false)
                .Append(self);

            return candidates
                .OrderBy(i => i.Metadata?.CreationTimestamp ?? DateTime.MaxValue)
                .ThenBy(i => i.Namespace(), StringComparer.Ordinal)
                .ThenBy(i => i.Name(), StringComparer.Ordinal)
                .First();
        }

        static bool IsSameResource(V1EmailProvider a, V1EmailProvider b)
        {
            if (string.IsNullOrEmpty(a.Uid()) == false && string.IsNullOrEmpty(b.Uid()) == false)
                return a.Uid() == b.Uid();

            return a.Namespace() == b.Namespace() && a.Name() == b.Name();
        }

        /// <inheritdoc />
        protected override async Task<Hashtable?> Get(
            IManagementApiClient api,
            string id,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            try
            {
                var self = await api.EmailProvider.GetAsync(GetFields, true, cancellationToken);
                if (self is null)
                    return null;

                return StripCredentials(TransformToSystemTextJson<Hashtable>(self));
            }
            catch (ErrorApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        /// <inheritdoc />
        protected override async Task<string?> Find(
            IManagementApiClient api,
            V1EmailProvider entity,
            V1EmailProvider.SpecDef spec,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            var self = await Get(api, ProviderId, defaultNamespace, cancellationToken);
            if (self is null)
            {
                Logger.LogInformation(
                    "{EntityTypeName} {EntityNamespace}/{EntityName} tenant has no email provider configured.",
                    EntityTypeName,
                    entity.Namespace(),
                    entity.Name()
                );
                return null;
            }

            Logger.LogInformation(
                "{EntityTypeName} {EntityNamespace}/{EntityName} found existing email provider {Name}.",
                EntityTypeName,
                entity.Namespace(),
                entity.Name(),
                self["name"]
            );
            return ProviderId;
        }

        /// <inheritdoc />
        protected override string? ValidateCreate(EmailProviderConf conf)
        {
            if (string.IsNullOrWhiteSpace(conf.Name))
                return "missing a value for name";

            return null;
        }

        /// <inheritdoc />
        protected override async Task<string> Create(
            IManagementApiClient api,
            EmailProviderConf conf,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            var request = new EmailProviderConfigureRequest();
            await PopulateRequestAsync(request, conf, defaultNamespace, cancellationToken);

            Logger.LogInformation(
                "{EntityTypeName} configuring email provider in Auth0 with name: {Name}",
                EntityTypeName,
                conf.Name
            );
            await api.EmailProvider.ConfigureAsync(request, cancellationToken);
            return ProviderId;
        }

        /// <inheritdoc />
        protected override async Task Update(
            IManagementApiClient api,
            string id,
            Hashtable? last,
            EmailProviderConf conf,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            // Auth0 never returns credentials, so they cannot be diffed; always send the resolved values.
            // This is also how a rotated Secret reaches Auth0 on the periodic reconcile.
            var request = new EmailProviderUpdateRequest();
            await PopulateRequestAsync(request, conf, defaultNamespace, cancellationToken);

            Logger.LogInformation(
                "{EntityTypeName} updating email provider in Auth0 with name: {Name}",
                EntityTypeName,
                conf.Name
            );
            await api.EmailProvider.UpdateAsync(request, cancellationToken);
        }

        /// <inheritdoc />
        protected override Task ApplyStatus(
            IManagementApiClient api,
            V1EmailProvider entity,
            Hashtable lastConf,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            return base.ApplyStatus(
                api,
                entity,
                StripCredentials(lastConf),
                defaultNamespace,
                cancellationToken
            );
        }

        /// <inheritdoc />
        protected override Task Delete(
            IManagementApiClient api,
            string id,
            CancellationToken cancellationToken
        )
        {
            Logger.LogInformation("{EntityTypeName} deleting email provider from Auth0.", EntityTypeName);
            return api.EmailProvider.DeleteAsync(cancellationToken);
        }

        /// <summary>
        /// Removes any credentials from a configuration read back from Auth0 before it is written to status.
        /// </summary>
        static Hashtable StripCredentials(Hashtable conf)
        {
            conf.Remove("credentials");
            return conf;
        }

        /// <summary>
        /// Copies the configuration onto the request, resolving credentials from Secrets.
        /// </summary>
        async Task PopulateRequestAsync(
            EmailProviderBase request,
            EmailProviderConf conf,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            // the SDK models declare these as non-nullable, but a null value is omitted from the request
#pragma warning disable CS8601
            request.Name = conf.Name;
            request.IsEnabled = conf.Enabled;
            request.DefaultFromAddress = conf.DefaultFromAddress;
            request.Credentials = await ResolveCredentialsAsync(
                conf.Credentials,
                defaultNamespace,
                cancellationToken
            );
            request.Settings = conf.Settings;
#pragma warning restore CS8601
        }

        /// <summary>
        /// Resolves every configured credential. Throws <see cref="RetryException"/> if any referenced Secret
        /// or key is missing or empty, so partial credentials are never sent.
        /// </summary>
        async Task<EmailProviderCredentialsRequest?> ResolveCredentialsAsync(
            EmailProviderCredentialsConf? conf,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            if (conf is null)
                return null;

            async Task<string?> R(EmailProviderCredentialValue? value, string field) =>
                await ResolveCredentialValueAsync(value, field, defaultNamespace, cancellationToken);

            var smtpPort = await R(conf.SmtpPort, "smtp_port");

            // the SDK model declares these as non-nullable, but a null value is omitted from the request
#pragma warning disable CS8601
            return new EmailProviderCredentialsRequest
            {
                ApiUser = await R(conf.ApiUser, "api_user"),
                ApiKey = await R(conf.ApiKey, "api_key"),
                AccessKeyId = await R(conf.AccessKeyId, "accessKeyId"),
                SecretAccessKey = await R(conf.SecretAccessKey, "secretAccessKey"),
                Region = await R(conf.Region, "region"),
                Domain = await R(conf.Domain, "domain"),
                SmtpHost = await R(conf.SmtpHost, "smtp_host"),
                SmtpPort = smtpPort is null ? null : ParsePort(smtpPort),
                SmtpUsername = await R(conf.SmtpUser, "smtp_user"),
                SmtpPassword = await R(conf.SmtpPass, "smtp_pass"),
                ConnectionString = await R(conf.ConnectionString, "connectionString"),
                TenantId = await R(conf.TenantId, "tenantId"),
                ClientId = await R(conf.ClientId, "clientId"),
                ClientSecret = await R(conf.ClientSecret, "clientSecret"),
            };
#pragma warning restore CS8601
        }

        static int ParsePort(string value)
        {
            if (int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) == false)
                throw new RetryException("Email provider credential smtp_port is not an integer.");

            return port;
        }

        /// <summary>
        /// Resolves a single credential from its literal value or Secret reference. Exception messages name the
        /// field and Secret but never include the value.
        /// </summary>
        async Task<string?> ResolveCredentialValueAsync(
            EmailProviderCredentialValue? value,
            string field,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            if (value is null)
                return null;

            if (value.SecretKeyRef is { } secretRef)
            {
                if (value.Value is not null)
                    throw new RetryException(
                        $"Email provider credential {field} sets both value and secretKeyRef; set only one."
                    );

                if (string.IsNullOrWhiteSpace(secretRef.Name) || string.IsNullOrWhiteSpace(secretRef.Key))
                    throw new RetryException(
                        $"Email provider credential {field} has a secretKeyRef without a name and key."
                    );

                var secret = await Kube.GetAsync<V1Secret>(
                    secretRef.Name,
                    defaultNamespace,
                    cancellationToken
                );
                if (secret is null)
                    throw new RetryException(
                        $"Email provider credential {field} references Secret {defaultNamespace}/{secretRef.Name}, which does not exist."
                    );

                if (secret.Data is null || secret.Data.TryGetValue(secretRef.Key, out var buf) == false)
                    throw new RetryException(
                        $"Email provider credential {field} references key {secretRef.Key} in Secret {defaultNamespace}/{secretRef.Name}, which does not exist."
                    );

                var resolved = buf is null ? "" : Encoding.UTF8.GetString(buf);
                if (string.IsNullOrWhiteSpace(resolved))
                    throw new RetryException(
                        $"Email provider credential {field} references key {secretRef.Key} in Secret {defaultNamespace}/{secretRef.Name}, which is empty."
                    );

                return resolved;
            }

            if (string.IsNullOrWhiteSpace(value.Value))
                throw new RetryException(
                    $"Email provider credential {field} has neither a value nor a secretKeyRef."
                );

            return value.Value;
        }
    }

    /// <summary>
    /// Email provider credentials, extended with the fields Auth0 accepts that the SDK model lacks.
    /// </summary>
    public class EmailProviderCredentialsRequest : EmailProviderCredentials
    {
        /// <summary>
        /// Domain (mailgun).
        /// </summary>
        [JsonProperty("domain")]
        public string? Domain { get; set; }

        /// <summary>
        /// Connection string (azure_cs).
        /// </summary>
        [JsonProperty("connectionString")]
        public string? ConnectionString { get; set; }

        /// <summary>
        /// Directory tenant ID (ms365).
        /// </summary>
        [JsonProperty("tenantId")]
        public string? TenantId { get; set; }

        /// <summary>
        /// Application client ID (ms365).
        /// </summary>
        [JsonProperty("clientId")]
        public string? ClientId { get; set; }

        /// <summary>
        /// Application client secret (ms365).
        /// </summary>
        [JsonProperty("clientSecret")]
        public string? ClientSecret { get; set; }
    }
}
