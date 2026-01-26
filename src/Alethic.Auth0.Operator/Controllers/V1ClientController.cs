using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Alethic.Auth0.Operator.Core.Models;
using Alethic.Auth0.Operator.Core.Models.Client;
using Alethic.Auth0.Operator.Models;
using Alethic.Auth0.Operator.Options;
using Alethic.Auth0.Operator.RateLimiting;

using Auth0.Core.Exceptions;
using Auth0.ManagementApi;
using Auth0.ManagementApi.Models;
using Auth0.ManagementApi.Models.Connections;

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
    [EntityRbac(typeof(V1Connection), Verbs = RbacVerb.List | RbacVerb.Get | RbacVerb.Update)]
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
        /// Clients can make many API calls due to enabled_connections reconciliation.
        /// Estimate: 2 base + up to 10 for connection enable/disable operations.
        /// </summary>
        protected override int EstimatedApiCalls => 12;

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

                var list = await api.Clients.GetAllAsync(new GetClientsRequest() { Fields = "client_id,name" }, cancellationToken: cancellationToken);
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
            // Always attempt to apply secret if secretRef is specified, regardless of whether we have the clientSecret value
            // This ensures secret resources are created for existing clients even when Auth0 API doesn't return the secret
            if (entity.Spec.SecretRef is not null)
            {
                var clientId = (string?)lastConf["client_id"];
                var clientSecret = (string?)lastConf["client_secret"];
                await ApplySecret(entity, clientId, clientSecret, defaultNamespace, cancellationToken);
            }

            // Handle enabled connections (add new ones and remove old ones)
            var clientId2 = (string?)lastConf["client_id"];
            if (clientId2 is not null)
            {
                await ReconcileEnabledConnections(api, entity, clientId2, defaultNamespace, cancellationToken);
            }

            lastConf.Remove("client_id");
            lastConf.Remove("client_secret");
            await base.ApplyStatus(api, entity, lastConf, defaultNamespace, cancellationToken);
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
            var secret = await ResolveClientSecretRef(entity.Spec.SecretRef, entity.Spec.SecretRef.NamespaceProperty ?? defaultNamespace, cancellationToken);
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

                // Track the effective clientId and clientSecret values for JSON output
                string effectiveClientId;
                string effectiveClientSecret;

                // Always set clientId if available
                if (clientId is not null)
                {
                    secret.StringData["clientId"] = clientId;
                    effectiveClientId = clientId;
                    Logger.LogDebug("{EntityTypeName} {EntityNamespace}/{EntityName} updated secret {SecretName} with clientId", EntityTypeName, entity.Namespace(), entity.Name(), entity.Spec.SecretRef.Name);
                }
                else if (!secret.StringData.ContainsKey("clientId"))
                {
                    // Initialize empty clientId field if not present and no value available
                    secret.StringData["clientId"] = "";
                    effectiveClientId = "";
                    Logger.LogDebug("{EntityTypeName} {EntityNamespace}/{EntityName} initialized empty clientId in secret {SecretName}", EntityTypeName, entity.Namespace(), entity.Name(), entity.Spec.SecretRef.Name);
                }
                else
                {
                    effectiveClientId = secret.StringData["clientId"];
                }

                // Handle clientSecret - for existing clients, Auth0 API doesn't return the secret
                if (clientSecret is not null)
                {
                    secret.StringData["clientSecret"] = clientSecret;
                    effectiveClientSecret = clientSecret;
                    Logger.LogDebug("{EntityTypeName} {EntityNamespace}/{EntityName} updated secret {SecretName} with clientSecret", EntityTypeName, entity.Namespace(), entity.Name(), entity.Spec.SecretRef.Name);
                }
                else if (!secret.StringData.ContainsKey("clientSecret"))
                {
                    // Initialize empty clientSecret field if not present and no value available
                    // Note: For existing clients, Auth0 API doesn't return the secret value for security reasons
                    secret.StringData["clientSecret"] = "";
                    effectiveClientSecret = "";
                    Logger.LogDebug("{EntityTypeName} {EntityNamespace}/{EntityName} initialized empty clientSecret in secret {SecretName} (Auth0 API does not return secrets for existing clients)", EntityTypeName, entity.Namespace(), entity.Name(), entity.Spec.SecretRef.Name);
                }
                else
                {
                    effectiveClientSecret = secret.StringData["clientSecret"];
                }

                // If format is "json", add a JSON key containing both clientId and clientSecret
                if (string.Equals(entity.Spec.SecretRef.Format, "json", StringComparison.OrdinalIgnoreCase))
                {
                    var jsonKeyName = entity.Spec.SecretRef.JsonKey ?? "credentials";
                    var credentialsJson = JsonSerializer.Serialize(new
                    {
                        clientId = effectiveClientId,
                        clientSecret = effectiveClientSecret
                    });
                    secret.StringData[jsonKeyName] = credentialsJson;
                    Logger.LogDebug("{EntityTypeName} {EntityNamespace}/{EntityName} updated secret {SecretName} with JSON key {JsonKeyName}", EntityTypeName, entity.Namespace(), entity.Name(), entity.Spec.SecretRef.Name, jsonKeyName);
                }

                secret = await Kube.UpdateAsync(secret, cancellationToken);
                Logger.LogInformation("{EntityTypeName} {EntityNamespace}/{EntityName} successfully updated secret {SecretName}", EntityTypeName, entity.Namespace(), entity.Name(), entity.Spec.SecretRef.Name);
            }
            else
            {
                Logger.LogInformation("{EntityTypeName} {EntityNamespace}/{EntityName} secret {SecretName} exists but is not owned by this client, skipping update", EntityTypeName, entity.Namespace(), entity.Name(), entity.Spec.SecretRef.Name);
            }
        }

        /// <summary>
        /// Attempts to resolve the list of connection references to connection IDs.
        /// </summary>
        /// <param name="api"></param>
        /// <param name="refs"></param>
        /// <param name="defaultNamespace"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        async Task<string[]?> ResolveConnectionRefsToIds(IManagementApiClient api, V1ConnectionReference[]? refs, string defaultNamespace, CancellationToken cancellationToken)
        {
            if (refs is null || refs.Length == 0)
                return Array.Empty<string>();

            var l = new List<string>(refs.Length);

            foreach (var i in refs)
                l.Add(await ResolveConnectionRefToId(api, i, defaultNamespace, cancellationToken) ?? throw new InvalidOperationException());

            return l.ToArray();
        }

        /// <summary>
        /// Reconciles enabled connections by comparing current desired state with previous state,
        /// enabling new connections and disabling removed ones.
        /// </summary>
        /// <param name="api"></param>
        /// <param name="entity"></param>
        /// <param name="clientId"></param>
        /// <param name="defaultNamespace"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        async Task ReconcileEnabledConnections(IManagementApiClient api, V1Client entity, string clientId, string defaultNamespace, CancellationToken cancellationToken)
        {
            var conf = entity.Spec.Conf;
            var currentConnectionRefs = conf?.EnabledConnections ?? Array.Empty<V1ConnectionReference>();
            var previousConnectionIds = entity.Status.LastEnabledConnectionIds ?? Array.Empty<string>();

            // Include any pending operations from previous reconciliation attempts
            var pendingEnableIds = entity.Status.PendingEnableConnectionIds ?? Array.Empty<string>();
            var pendingDisableIds = entity.Status.PendingDisableConnectionIds ?? Array.Empty<string>();

            // Resolve current connection references to IDs
            string[] currentConnectionIds;
            try
            {
                currentConnectionIds = await ResolveConnectionRefsToIds(api, currentConnectionRefs, defaultNamespace, cancellationToken) ?? Array.Empty<string>();
                currentConnectionIds = currentConnectionIds
                    .Where(id => string.IsNullOrWhiteSpace(id) == false)
                    .Distinct()
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray();
            }
            catch (RetryException)
            {
                // Connection not ready yet, will retry later
                Logger.LogWarning("{EntityTypeName} {ClientId} one or more referenced connections are not ready, will retry", EntityTypeName, clientId);
                throw;
            }

            // Find connections to enable (in current but not in previous, plus pending from previous attempts)
            var normalizedPreviousConnectionIds = previousConnectionIds
                .Where(id => string.IsNullOrWhiteSpace(id) == false)
                .Distinct()
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            var connectionsToEnable = currentConnectionIds
                .Except(normalizedPreviousConnectionIds)
                .Union(pendingEnableIds)
                .Distinct()
                .ToList();

            // Find connections to disable (in previous but not in current, plus pending from previous attempts)
            var connectionsToDisable = normalizedPreviousConnectionIds
                .Except(currentConnectionIds)
                .Union(pendingDisableIds)
                .Distinct()
                .ToList();

            // Track results
            var enabledSuccessfully = new List<string>();
            var disabledSuccessfully = new List<string>();
            var stillPendingEnable = new List<string>();
            var stillPendingDisable = new List<string>();
            RateLimitApiException? rateLimitException = null;
            var otherFailures = new List<Exception>();

            // Enable new connections
            foreach (var connectionId in connectionsToEnable)
            {
                try
                {
                    await EnableClientOnConnection(api, clientId, connectionId, cancellationToken);
                    enabledSuccessfully.Add(connectionId);
                }
                catch (RateLimitApiException ex)
                {
                    rateLimitException ??= ex; // Keep the first one for timing info
                    stillPendingEnable.Add(connectionId);
                    Logger.LogWarning("{EntityTypeName} {ClientId} hit rate limit enabling connection {ConnectionId}, will retry", EntityTypeName, clientId, connectionId);
                    // Stop processing more enables on rate limit
                    break;
                }
                catch (Exception ex)
                {
                    otherFailures.Add(ex);
                }
            }

            // Add remaining connections to pending if we hit a rate limit
            if (rateLimitException != null)
            {
                var processedCount = enabledSuccessfully.Count + stillPendingEnable.Count + otherFailures.Count;
                var remaining = connectionsToEnable.Skip(processedCount).ToList();
                stillPendingEnable.AddRange(remaining);
            }

            // Disable removed connections (only if we haven't hit a rate limit)
            if (rateLimitException == null)
            {
                foreach (var connectionId in connectionsToDisable)
                {
                    try
                    {
                        await DisableClientOnConnection(api, clientId, connectionId, cancellationToken);
                        disabledSuccessfully.Add(connectionId);
                    }
                    catch (RateLimitApiException ex)
                    {
                        rateLimitException ??= ex;
                        stillPendingDisable.Add(connectionId);
                        Logger.LogWarning("{EntityTypeName} {ClientId} hit rate limit disabling connection {ConnectionId}, will retry", EntityTypeName, clientId, connectionId);
                        break;
                    }
                    catch (Exception ex)
                    {
                        otherFailures.Add(ex);
                    }
                }

                // Add remaining connections to pending if we hit a rate limit
                if (rateLimitException != null)
                {
                    var processedCount = disabledSuccessfully.Count + stillPendingDisable.Count;
                    var remaining = connectionsToDisable.Skip(processedCount).ToList();
                    stillPendingDisable.AddRange(remaining);
                }
            }
            else
            {
                // All disable operations are pending since we already hit rate limit
                stillPendingDisable.AddRange(connectionsToDisable);
            }

            // Update status with partial progress
            // This ensures we don't lose track of what was successfully enabled/disabled
            var newEnabledList = normalizedPreviousConnectionIds
                .Except(disabledSuccessfully)
                .Union(enabledSuccessfully)
                .Distinct()
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            entity.Status.LastEnabledConnectionIds = newEnabledList;
            entity.Status.PendingEnableConnectionIds = stillPendingEnable.Count > 0 ? stillPendingEnable.ToArray() : null;
            entity.Status.PendingDisableConnectionIds = stillPendingDisable.Count > 0 ? stillPendingDisable.ToArray() : null;

            // If we hit a rate limit, throw it to trigger proper handling with backoff
            // But first log any other failures so they're not silently dropped
            if (rateLimitException != null)
            {
                if (otherFailures.Count > 0)
                {
                    Logger.LogWarning(
                        "{EntityTypeName} {ClientId} had {FailureCount} non-rate-limit failures that will be retried after rate limit backoff",
                        EntityTypeName,
                        clientId,
                        otherFailures.Count
                    );
                    foreach (var failure in otherFailures)
                    {
                        Logger.LogWarning(
                            failure,
                            "{EntityTypeName} {ClientId} deferred failure: {Message}",
                            EntityTypeName,
                            clientId,
                            failure.Message
                        );
                    }
                }
                throw rateLimitException;
            }

            // If there were other failures, throw aggregate exception
            if (otherFailures.Count > 0)
            {
                throw new AggregateException("One or more enabled connections could not be reconciled.", otherFailures);
            }
        }

        /// <summary>
        /// Enables this client on a specific connection.
        /// </summary>
        /// <param name="api"></param>
        /// <param name="clientId"></param>
        /// <param name="connectionId"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        async Task EnableClientOnConnection(IManagementApiClient api, string clientId, string connectionId, CancellationToken cancellationToken)
        {
            try
            {
                Logger.LogInformation("{EntityTypeName} {ClientId} enabling connection {ConnectionId}", EntityTypeName, clientId, connectionId);

                var request = new EnabledClientsUpdateRequest
                {
                    EnabledClients = new[]
                    {
                        new EnabledClientsToUpdate
                        {
                            ClientId = clientId,
                            Status = true
                        }
                    }
                };

                await api.Connections.UpdateEnabledClientsAsync(connectionId, request, cancellationToken);
                Logger.LogInformation("{EntityTypeName} {ClientId} successfully enabled connection {ConnectionId}", EntityTypeName, clientId, connectionId);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "{EntityTypeName} {ClientId} failed to enable connection {ConnectionId}", EntityTypeName, clientId, connectionId);
                throw;
            }
        }

        /// <summary>
        /// Disables this client on a specific connection.
        /// </summary>
        /// <param name="api"></param>
        /// <param name="clientId"></param>
        /// <param name="connectionId"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        async Task DisableClientOnConnection(IManagementApiClient api, string clientId, string connectionId, CancellationToken cancellationToken)
        {
            try
            {
                Logger.LogInformation("{EntityTypeName} {ClientId} disabling connection {ConnectionId} (reason: removed from enabled_connections)", EntityTypeName, clientId, connectionId);

                var request = new EnabledClientsUpdateRequest
                {
                    EnabledClients = new[]
                    {
                        new EnabledClientsToUpdate
                        {
                            ClientId = clientId,
                            Status = false
                        }
                    }
                };

                await api.Connections.UpdateEnabledClientsAsync(connectionId, request, cancellationToken);
                Logger.LogInformation("{EntityTypeName} {ClientId} successfully disabled connection {ConnectionId}", EntityTypeName, clientId, connectionId);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "{EntityTypeName} {ClientId} failed to disable connection {ConnectionId}", EntityTypeName, clientId, connectionId);
                throw;
            }
        }

        /// <inheritdoc />
        protected override async Task Delete(IManagementApiClient api, string id, CancellationToken cancellationToken)
        {
            // Note: We don't explicitly disable connections here because:
            // 1. The Delete method doesn't have access to the entity spec
            // 2. Connections are managed declaratively during reconciliation via ReconcileEnabledConnections
            // 3. If users want to clean up before deletion, they should remove enabled_connections from spec first,
            //    which will trigger reconciliation to disable the connections, then delete the client

            Logger.LogInformation("{EntityTypeName} deleting client from Auth0 with ID: {ClientId} (reason: Kubernetes entity deleted)", EntityTypeName, id);
            await api.Clients.DeleteAsync(id, cancellationToken);
            Logger.LogInformation("{EntityTypeName} successfully deleted client from Auth0 with ID: {ClientId}", EntityTypeName, id);
        }

    }

}
