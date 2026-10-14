using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Alethic.Auth0.Operator.Core.Models;
using Alethic.Auth0.Operator.Models;

using k8s;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Queue;
using KubeOps.KubernetesClient;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Alethic.Auth0.Operator.Services
{

    /// <summary>
    /// Background service that watches Client CRDs and triggers Connection reconciliation
    /// when a Client's enabledConnections change.
    /// 
    /// This uses the EntityRequeue delegate to trigger reconciliation. The previous
    /// "annotation-touch" pattern doesn't work because KubeOps 9.x uses generation-based
    /// filtering for MODIFIED events - annotation changes don't increment generation, so
    /// they get filtered out. Using EntityRequeue creates events that bypass generation
    /// checking since the entity is re-fetched from the API and passed to the controller.
    /// 
    /// On startup, the service pre-populates its cache from existing Clients and triggers
    /// an initial reconciliation of all referenced Connections. This ensures that even if
    /// the operator was restarted (or a new Client was created while the operator was down),
    /// all Connections will be properly reconciled with the correct enabled_clients.
    /// </summary>
    public class ClientConnectionWatcherService : BackgroundService
    {

        readonly IKubernetesClient _kube;
        readonly EntityRequeue<V1Connection> _requeue;
        readonly ILogger<ClientConnectionWatcherService> _logger;

        /// <summary>
        /// Track previous connection references for each client to detect removed connections.
        /// Key: "namespace/name" of the client
        /// Value: Set of normalized connection keys this client references
        /// </summary>
        readonly ConcurrentDictionary<string, HashSet<string>> _clientConnectionCache = new();

        /// <summary>
        /// Track whether each client has an Auth0 ID (is "ready").
        /// Used to detect when a Client transitions from not-ready to ready,
        /// which triggers re-reconciliation of all its connections.
        /// Key: "namespace/name" of the client
        /// Value: true if the client has status.id set
        /// </summary>
        readonly ConcurrentDictionary<string, bool> _clientReadyCache = new();

        /// <summary>
        /// Track connection-related labels on each client.
        /// Used to detect when labels change (e.g., after ApplyConnectionLabels),
        /// which may require re-triggering Connection reconciliation.
        /// Key: "namespace/name" of the client
        /// Value: Set of label keys matching connection ref labels
        /// </summary>
        readonly ConcurrentDictionary<string, HashSet<string>> _clientLabelCache = new();

        /// <summary>
        /// Label prefixes used for connection references. Used for precise filtering.
        /// </summary>
        static readonly string[] ConnectionLabelPrefixes = ["auth0.operator/conn-ref-", "auth0.operator/conn-id-"];

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="kube"></param>
        /// <param name="requeue"></param>
        /// <param name="logger"></param>
        public ClientConnectionWatcherService(
            IKubernetesClient kube,
            EntityRequeue<V1Connection> requeue,
            ILogger<ClientConnectionWatcherService> logger)
        {
            _kube = kube;
            _requeue = requeue;
            _logger = logger;
        }

        /// <inheritdoc />
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("ClientConnectionWatcherService starting");

            // Pre-populate cache from existing Clients and trigger initial reconciliation
            // This ensures that after an operator restart, all Connections are properly
            // reconciled with the correct enabled_clients.
            await InitializeCacheAndReconcileAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await WatchClientsAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Expected during shutdown
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Client watcher disconnected, reconnecting in 5 seconds");
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }

            _logger.LogInformation("ClientConnectionWatcherService stopped");
        }

        /// <summary>
        /// Pre-populates the connection cache from existing Clients and triggers an initial
        /// reconciliation of all referenced Connections. This is critical for ensuring that
        /// after an operator restart, all Connections are reconciled with the correct state.
        /// </summary>
        async Task InitializeCacheAndReconcileAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Initializing client connection cache from existing Clients");

                // List all existing Clients across all namespaces
                var existingClients = await _kube.ListAsync<V1Client>(@namespace: null, cancellationToken: cancellationToken);
                var allConnections = new HashSet<string>();

                foreach (var client in existingClients)
                {
                    var clientNs = client.Metadata.NamespaceProperty ?? "default";
                    var clientName = client.Metadata.Name ?? "";
                    var clientKey = $"{clientNs}/{clientName}";

                    // Build the set of connection references for this client
                    var connections = (client.Spec?.Conf?.EnabledConnections ?? [])
                        .Select(r => NormalizeConnectionReference(r, clientNs))
                        .Where(k => k != null)
                        .Cast<string>()
                        .ToHashSet();

                    // Track connection-related labels (only the specific prefixes we use)
                    var labels = (client.Metadata.Labels ?? new Dictionary<string, string>())
                        .Where(kv => ConnectionLabelPrefixes.Any(p => kv.Key.StartsWith(p)))
                        .Select(kv => kv.Key)
                        .ToHashSet();

                    // Add to caches
                    _clientConnectionCache[clientKey] = connections;
                    _clientReadyCache[clientKey] = !string.IsNullOrEmpty(client.Status?.Id);
                    _clientLabelCache[clientKey] = labels;

                    // Track all unique connections
                    allConnections.UnionWith(connections);

                    _logger.LogDebug("Cached Client {ClientKey} with {Count} connection references, {LabelCount} labels, ready: {Ready}",
                        clientKey, connections.Count, labels.Count, _clientReadyCache[clientKey]);
                }

                _logger.LogInformation("Initialized cache with {ClientCount} Clients referencing {ConnectionCount} unique Connections",
                    existingClients.Count, allConnections.Count);

                // Lazy-load connection list only if we have ID-based refs (avoids unnecessary API call)
                IList<V1Connection>? connectionCache = null;

                // Trigger initial reconciliation of ALL referenced connections
                // This ensures that any changes made while the operator was down are picked up
                foreach (var connKey in allConnections)
                {
                    // Only fetch connection list when we encounter an ID-based ref
                    if (connKey.StartsWith("id:") && connectionCache == null)
                    {
                        connectionCache = await _kube.ListAsync<V1Connection>(@namespace: null, cancellationToken: cancellationToken);
                    }
                    await RequeueConnectionByKeyAsync(connKey, "initial-sync", cancellationToken, connectionCache);
                }

                _logger.LogInformation("Completed initial sync - triggered reconciliation for {Count} Connections",
                    allConnections.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize client connection cache, will rely on watch events");
            }
        }

        /// <summary>
        /// Watches all Client CRDs across all namespaces.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        async Task WatchClientsAsync(CancellationToken cancellationToken)
        {
            _logger.LogDebug("Starting Client watch across all namespaces");

            await foreach (var (eventType, client) in
                _kube.WatchAsync<V1Client>(cancellationToken: cancellationToken))
            {
                try
                {
                    await OnClientChangedAsync(eventType, client, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing Client {Namespace}/{Name} change",
                        client.Metadata.NamespaceProperty, client.Metadata.Name);
                }
            }
        }

        /// <summary>
        /// Handles a Client change event by determining which Connections need reconciliation.
        /// Supports both name-based and ID-based connection references.
        /// 
        /// Key insight: We trigger reconciliation not just when connection references change,
        /// but also when a Client gets its Auth0 ID (status.id) for the first time. This handles
        /// the race condition where the Connection reconciles before the Client has an ID.
        /// </summary>
        /// <param name="eventType"></param>
        /// <param name="client"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        async Task OnClientChangedAsync(WatchEventType eventType, V1Client client, CancellationToken ct)
        {
            var clientNs = client.Metadata.NamespaceProperty ?? "default";
            var clientName = client.Metadata.Name ?? "";
            var clientKey = $"{clientNs}/{clientName}";

            // Get current connection references from the client spec
            // Use a normalized key format that handles both name-based and ID-based refs
            var currentConnections = (client.Spec?.Conf?.EnabledConnections ?? [])
                .Select(r => NormalizeConnectionReference(r, clientNs))
                .Where(k => k != null)
                .Cast<string>()
                .ToHashSet();

            // Get previous connections for this client
            _clientConnectionCache.TryGetValue(clientKey, out var previousConnections);
            previousConnections ??= new HashSet<string>();

            // Track whether this client has an Auth0 ID - used to detect "ready" state
            var hasAuth0Id = !string.IsNullOrEmpty(client.Status?.Id);
            var hadAuth0Id = _clientReadyCache.TryGetValue(clientKey, out var wasReady) && wasReady;

            // Track connection-related labels to detect when they change (only the specific prefixes we use)
            var currentLabels = (client.Metadata.Labels ?? new Dictionary<string, string>())
                .Where(kv => ConnectionLabelPrefixes.Any(p => kv.Key.StartsWith(p)))
                .Select(kv => kv.Key)
                .ToHashSet();
            _clientLabelCache.TryGetValue(clientKey, out var previousLabels);
            previousLabels ??= new HashSet<string>();
            var labelsChanged = !currentLabels.SetEquals(previousLabels);

            // Determine which connections need reconciliation
            HashSet<string> connectionsToReconcile;

            if (eventType == WatchEventType.Deleted)
            {
                // For deletions, reconcile all previously referenced connections
                connectionsToReconcile = previousConnections;
                _clientConnectionCache.TryRemove(clientKey, out _);
                _clientReadyCache.TryRemove(clientKey, out _);
                _clientLabelCache.TryRemove(clientKey, out _);
                _logger.LogDebug("Client {ClientKey} deleted, will reconcile {Count} previously referenced connections",
                    clientKey, connectionsToReconcile.Count);
            }
            else
            {
                // Find connections that were added or removed
                // Materialize to lists to avoid multiple enumeration when logging counts
                var addedConnections = currentConnections.Except(previousConnections).ToList();
                var removedConnections = previousConnections.Except(currentConnections).ToList();
                connectionsToReconcile = addedConnections.Union(removedConnections).ToHashSet();

                // CRITICAL: If the Client just became "ready" (got its Auth0 ID), trigger
                // reconciliation of ALL its connections. This handles the race condition where
                // the Connection reconciled before the Client had an ID to include.
                if (hasAuth0Id && !hadAuth0Id && currentConnections.Count > 0)
                {
                    _logger.LogDebug("Client {ClientKey} became ready (Auth0 ID: {Auth0Id}), will reconcile all {Count} referenced connections",
                        clientKey, client.Status?.Id, currentConnections.Count);
                    connectionsToReconcile.UnionWith(currentConnections);
                }

                // Also trigger if connection labels changed (handles edge case where labels
                // are applied after the ready transition due to timing or retry)
                if (labelsChanged && hasAuth0Id && currentConnections.Count > 0)
                {
                    _logger.LogDebug("Client {ClientKey} connection labels changed, will reconcile all {Count} referenced connections",
                        clientKey, currentConnections.Count);
                    connectionsToReconcile.UnionWith(currentConnections);
                }

                // Update caches
                _clientConnectionCache[clientKey] = currentConnections;
                _clientReadyCache[clientKey] = hasAuth0Id;
                _clientLabelCache[clientKey] = currentLabels;

                if (connectionsToReconcile.Count > 0)
                {
                    _logger.LogDebug("Client {ClientKey} changed, will reconcile {Count} connections (added: {Added}, removed: {Removed}, ready-trigger: {ReadyTrigger}, label-trigger: {LabelTrigger})",
                        clientKey, connectionsToReconcile.Count, addedConnections.Count, removedConnections.Count, hasAuth0Id && !hadAuth0Id, labelsChanged);
                }
            }

            // Trigger reconciliation via EntityRequeue
            foreach (var connKey in connectionsToReconcile)
            {
                await RequeueConnectionByKeyAsync(connKey, clientKey, ct);
            }
        }

        /// <summary>
        /// Normalizes a connection reference to a consistent key format.
        /// Returns "ref:{namespace}/{name}" for name-based refs, "id:{connectionId}" for ID-based refs.
        /// </summary>
        static string? NormalizeConnectionReference(V1ConnectionReference connRef, string defaultNamespace)
        {
            if (!string.IsNullOrEmpty(connRef.Id))
            {
                return $"id:{connRef.Id}";
            }
            else if (!string.IsNullOrEmpty(connRef.Name))
            {
                var ns = connRef.Namespace ?? defaultNamespace;
                return $"ref:{ns}/{connRef.Name}";
            }
            return null;
        }

        /// <summary>
        /// Requeues a connection for reconciliation based on the normalized key format.
        /// </summary>
        /// <param name="connKey">Normalized connection key (ref:ns/name or id:connectionId)</param>
        /// <param name="triggerSource">Source that triggered this reconciliation</param>
        /// <param name="ct">Cancellation token</param>
        /// <param name="connectionCache">Optional pre-fetched connection list for efficient ID lookups</param>
        async Task RequeueConnectionByKeyAsync(string connKey, string triggerSource, CancellationToken ct, IList<V1Connection>? connectionCache = null)
        {
            if (connKey.StartsWith("ref:"))
            {
                // Name-based reference: ref:{namespace}/{name}
                var refPart = connKey.Substring(4);
                var slashIndex = refPart.IndexOf('/');
                if (slashIndex > 0)
                {
                    var ns = refPart.Substring(0, slashIndex);
                    var name = refPart.Substring(slashIndex + 1);
                    await RequeueConnectionByNameAsync(ns, name, triggerSource, ct);
                }
                else
                {
                    _logger.LogWarning("Invalid connection reference key format: {ConnKey}", connKey);
                }
            }
            else if (connKey.StartsWith("id:"))
            {
                // ID-based reference: id:{connectionId}
                var connectionId = connKey.Substring(3);
                await RequeueConnectionByIdAsync(connectionId, triggerSource, ct, connectionCache);
            }
            else
            {
                _logger.LogWarning("Unknown connection key format: {ConnKey}", connKey);
            }
        }

        /// <summary>
        /// Enqueues a Connection for reconciliation by its Auth0 ID (status.id).
        /// Uses the EntityRequeue delegate which bypasses generation filtering since the
        /// entity is re-fetched from the API before being passed to the controller.
        /// </summary>
        /// <param name="connectionId">Auth0 connection ID to find</param>
        /// <param name="triggerSource">Source that triggered this reconciliation</param>
        /// <param name="ct">Cancellation token</param>
        /// <param name="connectionCache">Optional pre-fetched connection list for efficiency</param>
        async Task RequeueConnectionByIdAsync(string connectionId, string triggerSource, CancellationToken ct, IList<V1Connection>? connectionCache = null)
        {
            try
            {
                // Use cache if provided, otherwise fetch from API
                var allConnections = connectionCache ?? await _kube.ListAsync<V1Connection>(@namespace: null, cancellationToken: ct);
                var connection = allConnections.FirstOrDefault(c => c.Status?.Id == connectionId);

                if (connection == null)
                {
                    _logger.LogDebug("Connection with Auth0 ID {ConnectionId} not found, skipping requeue", connectionId);
                    return;
                }

                // Enqueue for immediate reconciliation using EntityRequeue
                // This bypasses KubeOps generation filtering because EntityRequeue re-fetches
                // the entity from the API before passing it to the controller
                _requeue(connection, TimeSpan.Zero);
                _logger.LogInformation("Triggered Connection {Namespace}/{Name} (Auth0 ID: {ConnectionId}) reconciliation due to Client change: {Source}",
                    connection.Metadata.NamespaceProperty, connection.Metadata.Name, connectionId, triggerSource);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enqueue Connection with Auth0 ID {ConnectionId}", connectionId);
            }
        }

        /// <summary>
        /// Enqueues a Connection for reconciliation by namespace and name.
        /// Uses the EntityRequeue delegate which bypasses generation filtering since the
        /// entity is re-fetched from the API before being passed to the controller.
        /// </summary>
        /// <param name="ns"></param>
        /// <param name="name"></param>
        /// <param name="triggerSource"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        async Task RequeueConnectionByNameAsync(string ns, string name, string triggerSource, CancellationToken ct)
        {
            try
            {
                var connection = await _kube.GetAsync<V1Connection>(name, ns, ct);
                if (connection == null)
                {
                    _logger.LogDebug("Connection {Namespace}/{Name} not found, skipping requeue", ns, name);
                    return;
                }

                // Enqueue for immediate reconciliation using EntityRequeue
                // This bypasses KubeOps generation filtering because EntityRequeue re-fetches
                // the entity from the API before passing it to the controller
                _requeue(connection, TimeSpan.Zero);
                _logger.LogInformation("Triggered Connection {Namespace}/{Name} reconciliation due to Client change: {Source}",
                    ns, name, triggerSource);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enqueue Connection {Namespace}/{Name}", ns, name);
            }
        }

    }

}
