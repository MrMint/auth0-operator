using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Alethic.Auth0.Operator.Core.Models;
using Alethic.Auth0.Operator.Core.Models.Client;
using Alethic.Auth0.Operator.Models;
using Alethic.Auth0.Operator.Options;
using Alethic.Auth0.Operator.Paging;
using Alethic.Auth0.Operator.RateLimiting;

using Auth0.Core.Exceptions;
using Auth0.ManagementApi;
using Auth0.ManagementApi.Models;

using k8s.Models;

using KubeOps.Abstractions.Controller;
using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Queue;
using KubeOps.Abstractions.Rbac;
using KubeOps.KubernetesClient;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alethic.Auth0.Operator.Controllers
{

    [EntityRbac(typeof(V1Tenant), Verbs = RbacVerb.List | RbacVerb.Get)]
    [EntityRbac(typeof(V1Client), Verbs = RbacVerb.All)]
    [EntityRbac(typeof(V1Connection), Verbs = RbacVerb.List | RbacVerb.Get)]
    [EntityRbac(typeof(V1Secret), Verbs = RbacVerb.All)]
    [EntityRbac(typeof(Eventsv1Event), Verbs = RbacVerb.All)]
    public class V1ClientController :
        V1TenantEntityController<V1Client, V1Client.SpecDef, V1Client.StatusDef, ClientConf>,
        IEntityController<V1Client>
    {

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
        public V1ClientController(
            IKubernetesClient kube,
            EntityRequeue<V1Client> requeue,
            IMemoryCache cache,
            ILogger<V1ClientController> logger,
            IOptions<OperatorOptions> options,
            IManagementApiClientFactory clientFactory,
            IRateLimiterService rateLimiterService,
            IReconciliationScheduler reconciliationScheduler
        )
            : base(kube, requeue, cache, logger, options, clientFactory, rateLimiterService, reconciliationScheduler)
        {

        }

        /// <inheritdoc />
        protected override string EntityTypeName => "Client";

        /// <summary>
        /// Base API calls for client operations (get/create/update).
        /// Note: enabled_clients is now managed by Connection controller.
        /// </summary>
        protected override int EstimatedApiCalls => 2;

        /// <inheritdoc />
        protected override async Task<Hashtable?> Get(IManagementApiClient api, string id, string defaultNamespace, CancellationToken cancellationToken)
        {
            try
            {
                return TransformToSystemTextJson<Hashtable>(await api.Clients.GetAsync(id, cancellationToken: cancellationToken));
            }
            catch (ErrorApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        /// <inheritdoc />
        protected override async Task<string?> Find(IManagementApiClient api, V1Client entity, V1Client.SpecDef spec, string defaultNamespace, CancellationToken cancellationToken)
        {
            if (spec.Find is not null)
            {
                if (spec.Find.ClientId is string clientId)
                {
                    try
                    {
                        var client = await api.Clients.GetAsync(clientId, "client_id,name", cancellationToken: cancellationToken);
                        Logger.LogInformation("{EntityTypeName} {EntityNamespace}/{EntityName} found existing client: {Name}", EntityTypeName, entity.Namespace(), entity.Name(), client.Name);
                        return client.ClientId;
                    }
                    catch (ErrorApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
                    {
                        Logger.LogInformation("{EntityTypeName} {EntityNamespace}/{EntityName} could not find client with id {ClientId}.", EntityTypeName, entity.Namespace(), entity.Name(), clientId);
                        return null;
                    }
                }

                return null;
            }
            else
            {
                var conf = spec.Init ?? spec.Conf;
                if (conf is null)
                    return null;

                // Offset, not checkpoint: Auth0 rejects checkpoint pagination on
                // GET /api/v2/clients unless the request also carries a q parameter. Narrowing by q
                // instead would be unsafe here — application names are not unique in Auth0, so a
                // search miss returns no match and we would silently create a duplicate app.
                var list = await Auth0Paging.GetAllOffsetPagesAsync<Client>(
                    pagination => api.Clients.GetAllAsync(new GetClientsRequest() { Fields = "client_id,name" }, pagination, cancellationToken),
                    cancellationToken);
                var self = list.FirstOrDefault(i => i.Name == conf.Name);
                return self?.ClientId;
            }
        }

        /// <inheritdoc />
        protected override string? ValidateCreate(ClientConf conf)
        {
            if (conf.ApplicationType == null)
                return "missing a value for application type";

            return null;
        }

        /// <inheritdoc />
        protected override async Task<string> Create(IManagementApiClient api, ClientConf conf, string defaultNamespace, CancellationToken cancellationToken)
        {
            Logger.LogInformation("{EntityTypeName} creating client in Auth0 with name: {ClientName}", EntityTypeName, conf.Name);
            var self = await api.Clients.CreateAsync(TransformToNewtonsoftJson<ClientConf, ClientCreateRequest>(conf), cancellationToken);
            Logger.LogInformation("{EntityTypeName} successfully created client in Auth0 with ID: {ClientId} and name: {ClientName}", EntityTypeName, self.ClientId, conf.Name);
            return self.ClientId;
        }

        /// <inheritdoc />
        protected override async Task Update(IManagementApiClient api, string id, Hashtable? last, ClientConf conf, string defaultNamespace, CancellationToken cancellationToken)
        {
            Logger.LogInformation("{EntityTypeName} updating client in Auth0 with id: {ClientId} and name: {ClientName}", EntityTypeName, id, conf.Name);

            // transform initial request
            var req = TransformToNewtonsoftJson<ClientConf, ClientUpdateRequest>(conf);

            // explicitely null out missing metadata if previously present
            if (last is not null && last.ContainsKey("client_metadata") && conf.ClientMetaData != null)
                foreach (string key in ((Hashtable)last["client_metadata"]!).Keys)
                    if (conf.ClientMetaData.ContainsKey(key) == false)
                        req.ClientMetaData[key] = null;

            await api.Clients.UpdateAsync(id, req, cancellationToken);
            Logger.LogInformation("{EntityTypeName} successfully updated client in Auth0 with id: {ClientId} and name: {ClientName}", EntityTypeName, id, conf.Name);
        }

        /// <inheritdoc />
        protected override async Task ApplyStatus(IManagementApiClient api, V1Client entity, Hashtable lastConf, string defaultNamespace, CancellationToken cancellationToken)
        {
            Logger.LogDebug("{EntityTypeName} {EntityNamespace}/{EntityName} ApplyStatus: entering, lastConf has {KeyCount} keys",
                EntityTypeName, entity.Namespace(), entity.Name(), lastConf?.Count ?? 0);

            // Always attempt to apply secret if secretRef is specified, regardless of whether we have the clientSecret value
            // This ensures secret resources are created for existing clients even when Auth0 API doesn't return the secret
            if (entity.Spec.SecretRef is not null)
            {
                var clientId = (string?)lastConf["client_id"];
                var clientSecret = (string?)lastConf["client_secret"];
                await ApplySecret(entity, clientId, clientSecret, defaultNamespace, cancellationToken);
            }

            // Apply labels for connection references to enable efficient reverse lookups
            // Connection controller uses these labels to find clients referencing a specific connection
            await ApplyConnectionLabels(entity, defaultNamespace, cancellationToken);

            lastConf.Remove("client_id");
            lastConf.Remove("client_secret");
            await base.ApplyStatus(api, entity, lastConf, defaultNamespace, cancellationToken);
        }

        /// <summary>
        /// Applies labels to the Client entity for reverse lookup by Connection controller.
        /// Uses hash-based label keys to stay within Kubernetes 63 char limit.
        /// Labels follow the pattern:
        /// - For name/namespace refs: auth0.operator/conn-ref-{hash} = "true"
        /// - For ID refs: auth0.operator/conn-id-{hash} = "true"
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="defaultNamespace"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        async Task ApplyConnectionLabels(V1Client entity, string defaultNamespace, CancellationToken cancellationToken)
        {
            var labelsChanged = false;
            entity.Metadata.Labels ??= new Dictionary<string, string>();

            // Clear old connection labels (both old format and new hash-based format)
            var labelsToRemove = entity.Metadata.Labels
                .Where(kv => kv.Key.StartsWith("auth0.operator/uses-connection-") ||
                             kv.Key.StartsWith("auth0.operator/conn-ref-") ||
                             kv.Key.StartsWith("auth0.operator/conn-id-"))
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in labelsToRemove)
            {
                entity.Metadata.Labels.Remove(key);
                labelsChanged = true;
            }

            // Add current connection labels using hash-based keys
            foreach (var connRef in entity.Spec?.Conf?.EnabledConnections ?? [])
            {
                string labelKey;

                if (!string.IsNullOrEmpty(connRef.Id))
                {
                    // ID-based reference: use conn-id-{hash}
                    labelKey = GenerateConnectionIdLabelKey(connRef.Id);
                    Logger.LogDebug("{EntityTypeName} {EntityNamespace}/{EntityName} adding ID-based connection label for ID {ConnectionId}",
                        EntityTypeName, entity.Namespace(), entity.Name(), connRef.Id);
                }
                else if (!string.IsNullOrEmpty(connRef.Name))
                {
                    // Name/namespace reference: use conn-ref-{hash}
                    var connNs = connRef.Namespace ?? defaultNamespace;
                    labelKey = GenerateConnectionLabelKey(connRef.Name, connNs);
                    Logger.LogDebug("{EntityTypeName} {EntityNamespace}/{EntityName} adding name-based connection label for {ConnectionNamespace}/{ConnectionName}",
                        EntityTypeName, entity.Namespace(), entity.Name(), connNs, connRef.Name);
                }
                else
                {
                    Logger.LogWarning("{EntityTypeName} {EntityNamespace}/{EntityName} skipping connection reference with no name or ID",
                        EntityTypeName, entity.Namespace(), entity.Name());
                    continue;
                }

                if (!entity.Metadata.Labels.ContainsKey(labelKey))
                {
                    entity.Metadata.Labels[labelKey] = "true";
                    labelsChanged = true;
                }
            }

            if (labelsChanged)
            {
                Logger.LogDebug("{EntityTypeName} {EntityNamespace}/{EntityName} updating connection labels",
                    EntityTypeName, entity.Namespace(), entity.Name());
                await Kube.UpdateAsync(entity, cancellationToken);
            }
        }

        /// <summary>
        /// Generates a hash-based label key for connection references.
        /// Format: auth0.operator/conn-ref-{hash} where hash is 16 chars.
        /// This ensures the label name (after prefix) stays under 63 chars.
        /// </summary>
        /// <param name="crdName">Connection CRD name</param>
        /// <param name="crdNamespace">Connection CRD namespace</param>
        /// <returns>Label key</returns>
        internal static string GenerateConnectionLabelKey(string crdName, string crdNamespace)
        {
            var input = $"{crdNamespace}/{crdName}";
            var hash = ComputeShortHash(input);
            return $"auth0.operator/conn-ref-{hash}";
        }

        /// <summary>
        /// Generates a hash-based label key for ID-based connection references.
        /// Format: auth0.operator/conn-id-{hash} where hash is 16 chars.
        /// </summary>
        /// <param name="connectionId">Auth0 connection ID</param>
        /// <returns>Label key</returns>
        internal static string GenerateConnectionIdLabelKey(string connectionId)
        {
            var hash = ComputeShortHash(connectionId);
            return $"auth0.operator/conn-id-{hash}";
        }

        /// <summary>
        /// Computes a short, stable hash for the given input.
        /// Returns first 16 characters of SHA256 hash in lowercase hex.
        /// </summary>
        /// <param name="input">Input string to hash</param>
        /// <returns>16-character hash string</returns>
        static string ComputeShortHash(string input)
        {
            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(hashBytes).Substring(0, 16).ToLowerInvariant();
        }

        /// <summary>
        /// Applies the client secret.
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="clientId"></param>
        /// <param name="clientSecret"></param>
        /// <param name="defaultNamespace"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        async Task ApplySecret(V1Client entity, string? clientId, string? clientSecret, string defaultNamespace, CancellationToken cancellationToken)
        {
            if (entity.Spec.SecretRef is null)
                return;

            // find existing secret or create
            var secret = await ResolveSecretRef(entity.Spec.SecretRef, entity.Spec.SecretRef.NamespaceProperty ?? defaultNamespace, cancellationToken);
            if (secret is null)
            {
                Logger.LogInformation("{EntityTypeName} {EntityNamespace}/{EntityName} referenced secret {SecretName} which does not exist: creating.", EntityTypeName, entity.Namespace(), entity.Name(), entity.Spec.SecretRef.Name);
                secret = await Kube.CreateAsync(
                    new V1Secret(
                        metadata: new V1ObjectMeta(namespaceProperty: entity.Spec.SecretRef.NamespaceProperty ?? defaultNamespace, name: entity.Spec.SecretRef.Name))
                        .WithOwnerReference(entity),
                    cancellationToken);
            }

            // only apply actual values if we are the owner
            if (secret.IsOwnedBy(entity))
            {
                Logger.LogInformation("{EntityTypeName} {EntityNamespace}/{EntityName} referenced secret {SecretName}: updating.", EntityTypeName, entity.Namespace(), entity.Name(), entity.Spec.SecretRef.Name);
                secret.StringData ??= new Dictionary<string, string>();

                // Always set clientId if available
                if (clientId is not null)
                {
                    secret.StringData["clientId"] = clientId;
                    Logger.LogDebug("{EntityTypeName} {EntityNamespace}/{EntityName} updated secret {SecretName} with clientId", EntityTypeName, entity.Namespace(), entity.Name(), entity.Spec.SecretRef.Name);
                }
                else if (!secret.StringData.ContainsKey("clientId"))
                {
                    // Initialize empty clientId field if not present and no value available
                    secret.StringData["clientId"] = "";
                    Logger.LogDebug("{EntityTypeName} {EntityNamespace}/{EntityName} initialized empty clientId in secret {SecretName}", EntityTypeName, entity.Namespace(), entity.Name(), entity.Spec.SecretRef.Name);
                }

                // Mirror clientId under the capitalized "clientID" key for consumers that
                // require that exact casing (e.g. the AWS Load Balancer Controller's
                // authenticate-oidc action). This is purely additive and backfills the key
                // onto pre-existing secrets that predate it. clientId is guaranteed present above.
                secret.StringData["clientID"] = secret.StringData["clientId"];

                // Handle clientSecret - for existing clients, Auth0 API doesn't return the secret
                if (clientSecret is not null)
                {
                    secret.StringData["clientSecret"] = clientSecret;
                    Logger.LogDebug("{EntityTypeName} {EntityNamespace}/{EntityName} updated secret {SecretName} with clientSecret", EntityTypeName, entity.Namespace(), entity.Name(), entity.Spec.SecretRef.Name);
                }
                else if (!secret.StringData.ContainsKey("clientSecret"))
                {
                    // Initialize empty clientSecret field if not present and no value available
                    // Note: For existing clients, Auth0 API doesn't return the secret value for security reasons
                    secret.StringData["clientSecret"] = "";
                    Logger.LogDebug("{EntityTypeName} {EntityNamespace}/{EntityName} initialized empty clientSecret in secret {SecretName} (Auth0 API does not return secrets for existing clients)", EntityTypeName, entity.Namespace(), entity.Name(), entity.Spec.SecretRef.Name);
                }

                secret = await Kube.UpdateAsync(secret, cancellationToken);
                Logger.LogInformation("{EntityTypeName} {EntityNamespace}/{EntityName} successfully updated secret {SecretName}", EntityTypeName, entity.Namespace(), entity.Name(), entity.Spec.SecretRef.Name);
            }
            else
            {
                Logger.LogInformation("{EntityTypeName} {EntityNamespace}/{EntityName} secret {SecretName} exists but is not owned by this client, skipping update", EntityTypeName, entity.Namespace(), entity.Name(), entity.Spec.SecretRef.Name);
            }
        }

        /// <inheritdoc />
        protected override async Task Delete(IManagementApiClient api, string id, CancellationToken cancellationToken)
        {
            // Note: enabled_clients is managed by the Connection controller via aggregation from all Client CRDs.
            // When this client is deleted, the ClientConnectionWatcherService will detect the deletion
            // and trigger reconciliation of affected Connection CRDs.

            Logger.LogInformation("{EntityTypeName} deleting client from Auth0 with ID: {ClientId} (reason: Kubernetes entity deleted)", EntityTypeName, id);
            await api.Clients.DeleteAsync(id, cancellationToken);
            Logger.LogInformation("{EntityTypeName} successfully deleted client from Auth0 with ID: {ClientId}", EntityTypeName, id);
        }

    }

}
