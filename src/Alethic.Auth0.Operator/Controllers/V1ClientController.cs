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
            // Diagnostic: Log entry into ApplyStatus
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

            // Handle enabled connections (add new ones and remove old ones)
            var clientId2 = (string?)lastConf["client_id"];
            Logger.LogDebug("{EntityTypeName} {EntityNamespace}/{EntityName} ApplyStatus: client_id from lastConf = {ClientId}",
                EntityTypeName, entity.Namespace(), entity.Name(), clientId2 ?? "(null)");

            if (clientId2 is not null)
            {
                await ReconcileEnabledConnections(api, entity, clientId2, defaultNamespace, cancellationToken);
            }
            else
            {
                Logger.LogWarning("{EntityTypeName} {EntityNamespace}/{EntityName} ApplyStatus: skipping ReconcileEnabledConnections because client_id is null in lastConf",
                    EntityTypeName, entity.Namespace(), entity.Name());
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

            // Diagnostic logging to trace enabled_connections deserialization
            // Serialize the entire Conf to JSON for inspection
            try
            {
                var confJson = conf != null ? JsonSerializer.Serialize(conf, new JsonSerializerOptions { WriteIndented = false }) : "(null)";
                Logger.LogDebug("{EntityTypeName} {ClientId} ReconcileEnabledConnections: Conf JSON = {ConfJson}", EntityTypeName, clientId, confJson);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "{EntityTypeName} {ClientId} ReconcileEnabledConnections: Failed to serialize Conf to JSON", EntityTypeName, clientId);
            }

            if (conf == null)
            {
                Logger.LogWarning("{EntityTypeName} {ClientId} ReconcileEnabledConnections: entity.Spec.Conf is NULL", EntityTypeName, clientId);
            }
            else if (conf.EnabledConnections == null)
            {
                Logger.LogWarning("{EntityTypeName} {ClientId} ReconcileEnabledConnections: EnabledConnections is NULL - check if enabled_connections is defined in spec.conf", EntityTypeName, clientId);
            }
            else if (conf.EnabledConnections.Length == 0)
            {
                Logger.LogInformation("{EntityTypeName} {ClientId} ReconcileEnabledConnections: EnabledConnections is empty array", EntityTypeName, clientId);
            }
            else
            {
                Logger.LogInformation("{EntityTypeName} {ClientId} ReconcileEnabledConnections: EnabledConnections has {Count} entries", EntityTypeName, clientId, conf.EnabledConnections.Length);
                foreach (var connRef in conf.EnabledConnections)
                {
                    Logger.LogInformation("{EntityTypeName} {ClientId} ReconcileEnabledConnections: Connection ref - Name={Name}, Namespace={Namespace}, Id={Id}",
                        EntityTypeName, clientId, connRef.Name ?? "(null)", connRef.Namespace ?? "(null)", connRef.Id ?? "(null)");
                }
            }

            var currentConnectionRefs = conf?.EnabledConnections ?? Array.Empty<V1ConnectionReference>();

            // Include any pending operations from previous reconciliation attempts (for rate limit recovery)
            var pendingEnableIds = entity.Status.PendingEnableConnectionIds ?? Array.Empty<string>();
            var pendingDisableIds = entity.Status.PendingDisableConnectionIds ?? Array.Empty<string>();

            // Resolve current connection references to IDs
            string[] desiredConnectionIds;
            try
            {
                desiredConnectionIds = await ResolveConnectionRefsToIds(api, currentConnectionRefs, defaultNamespace, cancellationToken) ?? Array.Empty<string>();
                desiredConnectionIds = desiredConnectionIds
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

            // Intersect pending operations with current desired state to handle spec changes during retry
            // If a connection was removed from spec, don't retry enabling it
            // If a connection was added to spec, don't retry disabling it
            var validPendingEnableIds = pendingEnableIds.Intersect(desiredConnectionIds).ToArray();
            var validPendingDisableIds = pendingDisableIds.Except(desiredConnectionIds).ToArray();

            // Determine which connections we need to query Auth0 for
            // We need to check: desired connections (to see if they need enabling) + valid pending disables (to verify they're still enabled)
            var connectionIdsToCheck = desiredConnectionIds
                .Union(validPendingDisableIds)
                .Distinct()
                .ToArray();

            // Get the ACTUAL currently enabled connections by querying Auth0 directly
            // This avoids staleness from Connection CRD status which isn't updated when Client controller enables/disables
            var actuallyEnabledConnectionIds = await GetActuallyEnabledConnections(api, clientId, connectionIdsToCheck, cancellationToken);

            // Connections to enable: desired but not actually enabled, plus any valid pending retries
            var connectionsToEnable = desiredConnectionIds
                .Except(actuallyEnabledConnectionIds)
                .Union(validPendingEnableIds.Except(actuallyEnabledConnectionIds))
                .Distinct()
                .ToList();

            // Connections to disable: actually enabled but not desired, plus any valid pending retries
            var connectionsToDisable = actuallyEnabledConnectionIds
                .Except(desiredConnectionIds)
                .Union(validPendingDisableIds.Intersect(actuallyEnabledConnectionIds))
                .Distinct()
                .ToList();

            // Diagnostic: Log the computed enable/disable lists
            Logger.LogInformation("{EntityTypeName} {ClientId} ReconcileEnabledConnections: desired={DesiredCount}, actuallyEnabled={ActualCount}, toEnable={EnableCount}, toDisable={DisableCount}",
                EntityTypeName, clientId, desiredConnectionIds.Length, actuallyEnabledConnectionIds.Length, connectionsToEnable.Count, connectionsToDisable.Count);

            if (connectionsToEnable.Count == 0 && connectionsToDisable.Count == 0)
            {
                Logger.LogDebug("{EntityTypeName} {ClientId} ReconcileEnabledConnections: no connection changes needed", EntityTypeName, clientId);
            }

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
                    // Track the failure AND mark the connection as pending for retry
                    otherFailures.Add(ex);
                    stillPendingEnable.Add(connectionId);
                    Logger.LogWarning(ex, "{EntityTypeName} {ClientId} failed to enable connection {ConnectionId}, will retry", EntityTypeName, clientId, connectionId);
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
                        // Track the failure AND mark the connection as pending for retry
                        otherFailures.Add(ex);
                        stillPendingDisable.Add(connectionId);
                        Logger.LogWarning(ex, "{EntityTypeName} {ClientId} failed to disable connection {ConnectionId}, will retry", EntityTypeName, clientId, connectionId);
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

            // Update status - only track pending operations for rate limit recovery
            // Auth0 is queried directly for actual state, so we only need to track pending retries
            entity.Status.PendingEnableConnectionIds = stillPendingEnable.Count > 0 ? stillPendingEnable.ToArray() : null;
            entity.Status.PendingDisableConnectionIds = stillPendingDisable.Count > 0 ? stillPendingDisable.ToArray() : null;

            // If we have pending operations (rate limit or failures), persist status BEFORE throwing
            // This ensures the pending state is saved to Kubernetes and will be retried on next reconciliation
            var hasPendingOperations = stillPendingEnable.Count > 0 || stillPendingDisable.Count > 0 || otherFailures.Count > 0;
            if (hasPendingOperations)
            {
                Logger.LogInformation(
                    "{EntityTypeName} {ClientId} has pending connection operations (enable: {PendingEnable}, disable: {PendingDisable}, failures: {Failures}), persisting status before retry",
                    EntityTypeName,
                    clientId,
                    stillPendingEnable.Count,
                    stillPendingDisable.Count,
                    otherFailures.Count
                );
                
                // Persist the status to Kubernetes so pending operations survive the exception
                await Kube.UpdateStatusAsync(entity, cancellationToken);
            }

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

            // If there were other failures, throw RetryException to trigger proper requeue
            // (AggregateException would fall through to generic exception handler which doesn't requeue)
            if (otherFailures.Count > 0)
            {
                var errorMessages = string.Join("; ", otherFailures.Select(e => e.Message));
                throw new RetryException($"One or more enabled connections could not be reconciled: {errorMessages}");
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

        /// <summary>
        /// Gets the Auth0 connection IDs where this client is actually enabled by querying Auth0 directly.
        /// This queries the Management API to get fresh data, avoiding staleness from Connection CRD status.
        /// </summary>
        /// <param name="api">Auth0 Management API client</param>
        /// <param name="clientId">The Auth0 client ID to check for</param>
        /// <param name="connectionIds">The connection IDs to check</param>
        /// <param name="cancellationToken"></param>
        /// <returns>Connection IDs where this client is currently enabled in Auth0</returns>
        async Task<string[]> GetActuallyEnabledConnections(IManagementApiClient api, string clientId, IEnumerable<string> connectionIds, CancellationToken cancellationToken)
        {
            var enabledOn = new List<string>();

            foreach (var connectionId in connectionIds)
            {
                try
                {
                    var connection = await api.Connections.GetAsync(connectionId, cancellationToken: cancellationToken);
                    if (connection?.EnabledClients != null && connection.EnabledClients.Contains(clientId))
                    {
                        enabledOn.Add(connectionId);
                        Logger.LogDebug("{EntityTypeName} {ClientId} GetActuallyEnabledConnections: Found enabled on {ConnectionId} ({ConnectionName})",
                            EntityTypeName, clientId, connectionId, connection.Name);
                    }
                }
                catch (ErrorApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    // Connection doesn't exist in Auth0 - skip it
                    Logger.LogWarning("{EntityTypeName} {ClientId} GetActuallyEnabledConnections: Connection {ConnectionId} not found in Auth0, skipping",
                        EntityTypeName, clientId, connectionId);
                }
                // Other exceptions propagate up to trigger retry
            }

            return enabledOn.ToArray();
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
