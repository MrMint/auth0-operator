using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Alethic.Auth0.Operator.Core.Models;
using Alethic.Auth0.Operator.Core.Models.Client;
using Alethic.Auth0.Operator.Core.Models.Connection;
using Alethic.Auth0.Operator.Models;
using Alethic.Auth0.Operator.Options;
using Alethic.Auth0.Operator.RateLimiting;

using Auth0.Core.Exceptions;
using Auth0.ManagementApi;
using Auth0.ManagementApi.Models;
using Auth0.ManagementApi.Paging;

using EnabledClientsGetRequest = Auth0.ManagementApi.Models.Connections.EnabledClientsGetRequest;
using EnabledClientsToUpdate = Auth0.ManagementApi.Models.Connections.EnabledClientsToUpdate;
using EnabledClientsUpdateRequest = Auth0.ManagementApi.Models.Connections.EnabledClientsUpdateRequest;

using k8s.Models;

using KubeOps.Abstractions.Controller;
using KubeOps.Abstractions.Queue;
using KubeOps.Abstractions.Rbac;
using KubeOps.KubernetesClient;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alethic.Auth0.Operator.Controllers
{

    [EntityRbac(typeof(V1Tenant), Verbs = RbacVerb.List | RbacVerb.Get)]
    [EntityRbac(typeof(V1Connection), Verbs = RbacVerb.All)]
    [EntityRbac(typeof(V1Client), Verbs = RbacVerb.List | RbacVerb.Get)]
    [EntityRbac(typeof(V1Secret), Verbs = RbacVerb.List | RbacVerb.Get)]
    [EntityRbac(typeof(Eventsv1Event), Verbs = RbacVerb.All)]
    public class V1ConnectionController :
        V1TenantEntityController<V1Connection, V1Connection.SpecDef, V1Connection.StatusDef, ConnectionConf>,
        IEntityController<V1Connection>
    {
        /// <summary>
        /// Page/batch size used for paginated reads and batched writes against
        /// the <c>/connections/{id}/clients</c> endpoint.
        /// </summary>
        const int PageSize = 50;

        /// <summary>
        /// Fields requested from <c>GET /api/v2/connections/{id}</c>.
        /// </summary>
        /// <remarks>
        /// This is an allow-list (<c>include_fields=true</c>) for two reasons:
        /// <list type="bullet">
        /// <item><description>enabled_clients is deprecated on this endpoint, and naming it in the
        /// fields parameter trips Auth0's deprecation detection even when it is only being excluded
        /// with <c>include_fields=false</c>. It is read from the dedicated
        /// <c>/connections/{id}/clients</c> endpoint instead.</description></item>
        /// <item><description>Auth0 validates this parameter against a fixed set and rejects the
        /// entire request with a 400 when it contains anything outside that set, so every entry here
        /// must be one Auth0 currently accepts. <c>realms</c> and <c>is_domain_connection</c> are no
        /// longer accepted and therefore cannot be read back at all.</description></item>
        /// </list>
        /// </remarks>
        const string GetFields = "id,name,display_name,strategy,show_as_button,provisioning_ticket_url,options,metadata";

        /// <summary>
        /// Holds the current entity being reconciled.
        /// Used to access CRD metadata name/namespace in Create/Update methods.
        /// </summary>
        readonly AsyncLocal<V1Connection?> _currentEntity = new();

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
        public V1ConnectionController(
            IKubernetesClient kube,
            EntityRequeue<V1Connection> requeue,
            IMemoryCache cache,
            ILogger<V1ConnectionController> logger,
            IOptions<OperatorOptions> options,
            IManagementApiClientFactory clientFactory,
            IRateLimiterService rateLimiterService,
            IReconciliationScheduler reconciliationScheduler
        )
            : base(kube, requeue, cache, logger, options, clientFactory, rateLimiterService, reconciliationScheduler)
        {

        }

        /// <inheritdoc />
        protected override string EntityTypeName => "Connection";

        /// <summary>
        /// Minimum estimate: +1 for GetEnabledClients in Get(), +1 for
        /// UpdateEnabledClients in Create()/Update(). Actual count may be
        /// higher when pagination or batching kicks in.
        /// </summary>
        protected override int EstimatedApiCalls => 4;

        /// <inheritdoc />
        protected override async Task<bool> Reconcile(V1Connection entity, CancellationToken cancellationToken)
        {
            // Capture entity for use in Create/Update methods
            _currentEntity.Value = entity;
            try
            {
                return await base.Reconcile(entity, cancellationToken);
            }
            finally
            {
                _currentEntity.Value = null;
            }
        }

        /// <inheritdoc />
        protected override async Task<Hashtable?> Get(IManagementApiClient api, string id, string defaultNamespace, CancellationToken cancellationToken)
        {
            try
            {
                // Request only the fields we read, via an allow-list. See GetFields for why.
                var self = await api.Connections.GetAsync(id, fields: GetFields, includeFields: true, cancellationToken: cancellationToken);
                if (self == null)
                    return null;

                var dict = new Hashtable();
                dict["id"] = self.Id;
                dict["name"] = self.Name;
                dict["display_name"] = self.DisplayName;
                dict["strategy"] = self.Strategy;

                // realms and is_domain_connection are absent by necessity: Auth0 no longer accepts
                // them in the fields allow-list, so they cannot be read back. They stay write-only,
                // applied from spec.conf by ApplyConfToRequest.
                dict["show_as_button"] = self.ShowAsButton;
                dict["provisioning_ticket_url"] = self.ProvisioningTicketUrl;

                // Fetch enabled_clients via the dedicated endpoint.
                dict["enabled_clients"] = await GetAllEnabledClientIdsAsync(api, id, cancellationToken);

                dict["options"] = TransformToSystemTextJson<Hashtable?>(self.Options);
                dict["metadata"] = TransformToSystemTextJson<Hashtable?>(self.Metadata);
                return dict;
            }
            catch (ErrorApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        /// <inheritdoc />
        protected override async Task<string?> Find(IManagementApiClient api, V1Connection entity, V1Connection.SpecDef spec, string defaultNamespace, CancellationToken cancellationToken)
        {
            if (spec.Find is not null)
            {
                if (spec.Find.ConnectionId is string connectionId)
                {
                    try
                    {
                        // Allow-list only the fields we read (id, name). Do not name enabled_clients
                        // — referencing it at all on GET /api/v2/connections/{id} trips deprecation detection.
                        var connection = await api.Connections.GetAsync(connectionId, fields: "id,name", includeFields: true, cancellationToken: cancellationToken);
                        Logger.LogInformation("{EntityTypeName} {EntityNamespace}/{EntityName} found existing connection: {Name}", EntityTypeName, entity.Namespace(), entity.Name(), connection.Name);
                        return connection.Id;
                    }
                    catch (ErrorApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
                    {
                        Logger.LogInformation("{EntityTypeName} {EntityNamespace}/{EntityName} could not find connection with id {ConnectionId}.", EntityTypeName, entity.Namespace(), entity.Name(), connectionId);
                        return null;
                    }
                }

                return null;
            }
            else
            {
                var conf = spec.Init ?? spec.Conf;
                if (conf is null || string.IsNullOrEmpty(conf.Name))
                    return null;

                // Allow-list only the fields we read (id, name). Do not name enabled_clients
                // — referencing it at all on GET /api/v2/connections trips deprecation detection.
                var list = await api.Connections.GetAllAsync(new GetConnectionsRequest { Fields = "id,name", IncludeFields = true }, (PaginationInfo?)null, cancellationToken);
                var self = list.FirstOrDefault(i => i.Name == conf.Name);
                if (self is not null)
                    Logger.LogInformation("{EntityTypeName} {EntityNamespace}/{EntityName} found existing connection by name: {Name}", EntityTypeName, entity.Namespace(), entity.Name(), conf.Name);

                return self?.Id;
            }
        }

        /// <inheritdoc />
        protected override string? ValidateCreate(ConnectionConf conf)
        {
            return null;
        }

        /// <inheritdoc />
        protected override async Task<string> Create(IManagementApiClient api, ConnectionConf conf, string defaultNamespace, CancellationToken cancellationToken)
        {
            Logger.LogInformation("{EntityTypeName} creating connection in Auth0 with name: {ConnectionName} and strategy: {Strategy}", EntityTypeName, conf.Name, conf.Strategy);
            var req = new ConnectionCreateRequest();
            ApplyConfToRequest(req, conf);

            if (conf.Strategy is null)
                throw new InvalidOperationException("Missing connection strategy.");

            // calculate options, depends on strategy
            var options = conf.Strategy == "auth0" ? (dynamic?)TransformToNewtonsoftJson<ConnectionOptions, global::Auth0.ManagementApi.Models.Connections.ConnectionOptions>(JsonSerializer.Deserialize<ConnectionOptions>(JsonSerializer.Serialize(conf.Options))) : conf.Options;
            if (options is null)
                throw new InvalidOperationException("Missing connection options.");

            // configure strategy and options
            req.Strategy = conf.Strategy;
            req.Options = options;

            // For new connections, aggregate enabled_clients from Client CRDs
            // Use CRD metadata name/namespace, not conf.Name (which is the Auth0 name)
            var entityName = _currentEntity.Value?.Name() ?? "";
            var entityNamespace = _currentEntity.Value?.Namespace() ?? defaultNamespace;
            var aggregatedClientIds = await AggregateEnabledClientsFromClientCRDs(
                entityName,
                entityNamespace,
                cancellationToken);

            var self = await api.Connections.CreateAsync(req, cancellationToken);
            if (self is null)
                throw new InvalidOperationException();

            // Sync enabled clients via the dedicated connection clients endpoint
            // (enabled_clients on ConnectionCreateRequest is deprecated)
            if (aggregatedClientIds.Length > 0)
            {
                try
                {
                    await SyncEnabledClientsAsync(api, self.Id, aggregatedClientIds, Array.Empty<string>(), cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Connection was created but client sync failed. Log at Warning level so
                    // operators have visibility. The next reconciliation loop will fix this
                    // via the Update path since the connection now has a status.id.
                    Logger.LogWarning(ex,
                        "{EntityTypeName} connection {ConnectionId} created successfully but enabled_clients sync failed — will retry on next reconciliation",
                        EntityTypeName, self.Id);
                }
            }

            Logger.LogInformation("{EntityTypeName} successfully created connection in Auth0 with ID: {ConnectionId}, name: {ConnectionName} and strategy: {Strategy}", EntityTypeName, self.Id, conf.Name, conf.Strategy);
            return self.Id;
        }

        /// <inheritdoc />
        protected override async Task Update(IManagementApiClient api, string id, Hashtable? last, ConnectionConf conf, string defaultNamespace, CancellationToken cancellationToken)
        {
            Logger.LogInformation("{EntityTypeName} updating connection in Auth0 with ID: {ConnectionId}, name: {ConnectionName} and strategy: {Strategy}", EntityTypeName, id, conf.Name, conf.Strategy);
            var req = new ConnectionUpdateRequest();
            ApplyConfToRequest(req, conf);

            // name has to be cleared for an update
            req.Name = null!;

            // Aggregate enabled_clients from all Client CRDs that reference this connection
            // This is the single source of truth for enabled_clients (Connection is the single writer)
            // Use CRD metadata name/namespace, not conf.Name (which is the Auth0 name)
            var entityName = _currentEntity.Value?.Name() ?? "";
            var entityNamespace = _currentEntity.Value?.Namespace() ?? defaultNamespace;
            var aggregatedClientIds = await AggregateEnabledClientsFromClientCRDs(
                entityName,
                entityNamespace,
                cancellationToken);

            Logger.LogDebug("{EntityTypeName} aggregated {Count} enabled_clients for connection {ConnectionId}",
                EntityTypeName, aggregatedClientIds.Length, id);

            // calculate options: depends on current strategy, possibly null, which means no apply
            var strategy = last?["strategy"] as string ?? conf.Strategy;
            var options = strategy == "auth0" && conf.Options is not null ? (dynamic?)TransformToNewtonsoftJson<ConnectionOptions, global::Auth0.ManagementApi.Models.Connections.ConnectionOptions>(JsonSerializer.Deserialize<ConnectionOptions>(JsonSerializer.Serialize(conf.Options))) : conf.Options;
            if (options is not null)
                req.Options = options;

            await api.Connections.UpdateAsync(id, req, cancellationToken);

            // Sync enabled clients via the dedicated connection clients endpoint
            // (enabled_clients on ConnectionUpdateRequest is deprecated)
            var currentEnabledClients = last?["enabled_clients"] as string[] ?? Array.Empty<string>();
            await SyncEnabledClientsAsync(api, id, aggregatedClientIds, currentEnabledClients, cancellationToken);

            Logger.LogInformation("{EntityTypeName} successfully updated connection in Auth0 with ID: {ConnectionId}, name: {ConnectionName} and strategy: {Strategy}", EntityTypeName, id, conf.Name, conf.Strategy);
        }

        /// <summary>
        /// Aggregates enabled client IDs from all Client CRDs that reference this connection.
        /// Uses label-based indexing for efficient lookups, plus ID-based reference fallback.
        /// 
        /// Note: Race conditions (Client without labels yet) are handled by ClientConnectionWatcherService,
        /// which re-triggers Connection reconciliation when a Client becomes ready or its labels change.
        /// </summary>
        /// <param name="crdName">The Connection CRD's metadata.name</param>
        /// <param name="crdNamespace">The Connection CRD's metadata.namespace</param>
        /// <param name="cancellationToken"></param>
        /// <returns>Array of Auth0 client IDs that should be enabled on this connection</returns>
        async Task<string[]> AggregateEnabledClientsFromClientCRDs(
            string crdName,
            string crdNamespace,
            CancellationToken cancellationToken)
        {
            // Use label selector for efficient lookup by CRD name/namespace
            var clients = await GetClientsReferencingConnection(crdName, crdNamespace, cancellationToken);

            // Also find clients referencing this connection by Auth0 ID (if status.id is available)
            var connectionId = _currentEntity.Value?.Status?.Id;
            if (!string.IsNullOrEmpty(connectionId))
            {
                var clientsByIdRef = await GetClientsReferencingConnectionById(connectionId, cancellationToken);
                clients = clients.Concat(clientsByIdRef).ToList();
            }

            // Note: We do NOT need a fallback spec scan here. The ClientConnectionWatcherService
            // handles the race condition by re-triggering Connection reconciliation when a Client
            // becomes "ready" (gets its Auth0 ID). This is more efficient than scanning all Clients.

            var enabledClientIds = new List<string>();
            foreach (var client in clients)
            {
                if (string.IsNullOrEmpty(client.Status?.Id))
                {
                    Logger.LogDebug("{EntityTypeName} skipping Client {ClientNamespace}/{ClientName} - not yet created in Auth0",
                        EntityTypeName, client.Namespace(), client.Name());
                    continue;
                }

                enabledClientIds.Add(client.Status.Id);
                Logger.LogDebug("{EntityTypeName} including Client {ClientNamespace}/{ClientName} (Auth0 ID: {ClientId}) in enabled_clients",
                    EntityTypeName, client.Namespace(), client.Name(), client.Status.Id);
            }

            Logger.LogDebug("{EntityTypeName} aggregated {Count} enabled_clients for connection {ConnNamespace}/{ConnName}",
                EntityTypeName, enabledClientIds.Count, crdNamespace, crdName);

            // Use OrderBy for deterministic ordering, then Distinct
            return enabledClientIds.Distinct().OrderBy(id => id).ToArray();
        }

        /// <summary>
        /// Gets all Client CRDs that reference this connection using label selectors.
        /// Uses hash-based label keys to avoid Kubernetes 63 char limit.
        /// </summary>
        /// <param name="crdName">The Connection CRD's metadata.name</param>
        /// <param name="crdNamespace">The Connection CRD's metadata.namespace</param>
        /// <param name="cancellationToken"></param>
        /// <returns>List of V1Client resources referencing this connection</returns>
        async Task<IList<V1Client>> GetClientsReferencingConnection(
            string crdName,
            string crdNamespace,
            CancellationToken cancellationToken)
        {
            var labelKey = GenerateConnectionLabelKey(crdName, crdNamespace);
            var labelSelector = $"{labelKey}=true";

            Logger.LogDebug("{EntityTypeName} searching for Clients with label selector: {LabelSelector}",
                EntityTypeName, labelSelector);

            // Efficient API server query using label selector across all namespaces
            return await Kube.ListAsync<V1Client>(
                @namespace: null,
                labelSelector: labelSelector,
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Gets all Client CRDs that reference this connection by Auth0 ID.
        /// Uses hash-based label keys.
        /// </summary>
        /// <param name="connectionId">The Auth0 connection ID</param>
        /// <param name="cancellationToken"></param>
        /// <returns>List of V1Client resources referencing this connection by ID</returns>
        async Task<IList<V1Client>> GetClientsReferencingConnectionById(
            string connectionId,
            CancellationToken cancellationToken)
        {
            var labelKey = GenerateConnectionIdLabelKey(connectionId);
            var labelSelector = $"{labelKey}=true";

            Logger.LogDebug("{EntityTypeName} searching for Clients with ID-based label selector: {LabelSelector}",
                EntityTypeName, labelSelector);

            return await Kube.ListAsync<V1Client>(
                @namespace: null,
                labelSelector: labelSelector,
                cancellationToken: cancellationToken);
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
        /// Retrieves all enabled client IDs for a connection using the dedicated
        /// <c>/api/v2/connections/{id}/clients</c> endpoint.
        /// Handles checkpoint-based pagination to fetch all results.
        /// </summary>
        /// <param name="api">The Auth0 Management API client</param>
        /// <param name="connectionId">The Auth0 connection ID</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Array of Auth0 client IDs enabled on this connection</returns>
        async Task<string[]> GetAllEnabledClientIdsAsync(
            IManagementApiClient api,
            string connectionId,
            CancellationToken cancellationToken)
        {
            var allClientIds = new List<string>();
            string? from = null;

            while (true)
            {
                // CheckpointPaginationInfo treats null 'from' as "start from the beginning"
#pragma warning disable CS8625
                var pagination = new CheckpointPaginationInfo(PageSize, from!);
#pragma warning restore CS8625
                var page = await api.Connections.GetEnabledClientsAsync(
                    new EnabledClientsGetRequest { ConnectionId = connectionId },
                    pagination,
                    cancellationToken);

                foreach (var ec in page)
                {
                    if (!string.IsNullOrEmpty(ec.ClientId))
                        allClientIds.Add(ec.ClientId);
                }

                if (page.Paging?.Next == null)
                    break;

                from = page.Paging.Next;
            }

            return allClientIds.ToArray();
        }

        /// <summary>
        /// Syncs the enabled clients for a connection to the desired state using the dedicated
        /// <c>PATCH /api/v2/connections/{id}/clients</c> endpoint.
        /// Computes the diff between current and desired states and sends only the changes.
        /// Batches updates in chunks to avoid exceeding API payload limits.
        /// </summary>
        /// <param name="api">The Auth0 Management API client</param>
        /// <param name="connectionId">The Auth0 connection ID</param>
        /// <param name="desiredClientIds">The desired set of enabled client IDs</param>
        /// <param name="currentClientIds">The current set of enabled client IDs (from Get)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        async Task SyncEnabledClientsAsync(
            IManagementApiClient api,
            string connectionId,
            string[] desiredClientIds,
            string[] currentClientIds,
            CancellationToken cancellationToken)
        {
            var currentSet = new HashSet<string>(currentClientIds);
            var desiredSet = new HashSet<string>(desiredClientIds);

            var updates = new List<EnabledClientsToUpdate>();

            // Enable clients that should be enabled but aren't
            foreach (var clientId in desiredSet.Except(currentSet))
            {
                updates.Add(new EnabledClientsToUpdate { ClientId = clientId, Status = true });
                Logger.LogDebug("{EntityTypeName} enabling client {ClientId} on connection {ConnectionId}",
                    EntityTypeName, clientId, connectionId);
            }

            // Disable clients that are currently enabled but shouldn't be
            foreach (var clientId in currentSet.Except(desiredSet))
            {
                updates.Add(new EnabledClientsToUpdate { ClientId = clientId, Status = false });
                Logger.LogDebug("{EntityTypeName} disabling client {ClientId} on connection {ConnectionId}",
                    EntityTypeName, clientId, connectionId);
            }

            if (updates.Count > 0)
            {
                var toEnable = updates.Count(u => u.Status == true);
                var toDisable = updates.Count(u => u.Status == false);
                Logger.LogInformation(
                    "{EntityTypeName} syncing enabled client changes for connection {ConnectionId} ({Enable} to enable, {Disable} to disable)",
                    EntityTypeName, connectionId, toEnable, toDisable);

                // Send in batches to avoid exceeding API payload limits
                foreach (var batch in updates.Chunk(PageSize))
                {
                    await api.Connections.UpdateEnabledClientsAsync(
                        connectionId,
                        new EnabledClientsUpdateRequest { EnabledClients = batch.ToList() },
                        cancellationToken);
                }
            }
            else
            {
                Logger.LogDebug("{EntityTypeName} no enabled client changes needed for connection {ConnectionId}",
                    EntityTypeName, connectionId);
            }
        }

        /// <summary>
        /// Applies the specified configuration to the request object.
        /// Note: enabled_clients is not applied here - it's synced separately via the dedicated
        /// <c>/api/v2/connections/{id}/clients</c> endpoint.
        /// </summary>
        /// <param name="req"></param>
        /// <param name="conf"></param>
        void ApplyConfToRequest(ConnectionBase req, ConnectionConf conf)
        {
            if (conf.Name is null)
                throw new InvalidOperationException("Missing name.");

            req.Name = conf.Name;
            if (conf.DisplayName is not null)
                req.DisplayName = conf.DisplayName;
            req.Metadata = conf.Metadata ?? null!;
            req.Realms = conf.Realms ?? [];
            req.IsDomainConnection = conf.IsDomainConnection ?? false;
            req.ShowAsButton = conf.ShowAsButton;
            // Note: EnabledClients is intentionally NOT set here.
            // It's synced separately via the dedicated /api/v2/connections/{id}/clients endpoint.
        }

        /// <inheritdoc />
        protected override async Task Delete(IManagementApiClient api, string id, CancellationToken cancellationToken)
        {
            Logger.LogInformation("{EntityTypeName} deleting connection from Auth0 with ID: {ConnectionId} (reason: Kubernetes entity deleted)", EntityTypeName, id);
            await api.Connections.DeleteAsync(id, cancellationToken);
            Logger.LogInformation("{EntityTypeName} successfully deleted connection from Auth0 with ID: {ConnectionId}", EntityTypeName, id);
        }

    }

}
