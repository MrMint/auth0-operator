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
using KubeOps.KubernetesClient;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Alethic.Auth0.Operator.Services
{

    /// <summary>
    /// Background service that watches Client CRDs and triggers Connection reconciliation
    /// when a Client's enabledConnections change. This implements the "annotation-touch"
    /// pattern since KubeOps 9.x does not support cross-entity requeue.
    /// </summary>
    public class ClientConnectionWatcherService : BackgroundService
    {

        readonly IKubernetesClient _kube;
        readonly ILogger<ClientConnectionWatcherService> _logger;

        /// <summary>
        /// Track previous connection references for each client to detect removed connections.
        /// Key: "namespace/name" of the client
        /// Value: Set of "namespace/name" connection keys this client references
        /// </summary>
        readonly ConcurrentDictionary<string, HashSet<string>> _clientConnectionCache = new();

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="kube"></param>
        /// <param name="logger"></param>
        public ClientConnectionWatcherService(
            IKubernetesClient kube,
            ILogger<ClientConnectionWatcherService> logger)
        {
            _kube = kube;
            _logger = logger;
        }

        /// <inheritdoc />
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("ClientConnectionWatcherService starting");

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

            // Determine which connections need reconciliation
            HashSet<string> connectionsToReconcile;

            if (eventType == WatchEventType.Deleted)
            {
                // For deletions, reconcile all previously referenced connections
                connectionsToReconcile = previousConnections;
                _clientConnectionCache.TryRemove(clientKey, out _);
                _logger.LogDebug("Client {ClientKey} deleted, will reconcile {Count} previously referenced connections",
                    clientKey, connectionsToReconcile.Count);
            }
            else
            {
                // Find connections that were added or removed
                var addedConnections = currentConnections.Except(previousConnections);
                var removedConnections = previousConnections.Except(currentConnections);
                connectionsToReconcile = addedConnections.Union(removedConnections).ToHashSet();

                // Update cache
                _clientConnectionCache[clientKey] = currentConnections;

                if (connectionsToReconcile.Count > 0)
                {
                    _logger.LogDebug("Client {ClientKey} changed, will reconcile {Count} connections (added: {Added}, removed: {Removed})",
                        clientKey, connectionsToReconcile.Count, addedConnections.Count(), removedConnections.Count());
                }
            }

            // Trigger reconciliation via annotation-touch pattern
            foreach (var connKey in connectionsToReconcile)
            {
                await TouchConnectionByKeyAsync(connKey, clientKey, ct);
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
        /// Touches a connection to trigger reconciliation based on the normalized key format.
        /// </summary>
        async Task TouchConnectionByKeyAsync(string connKey, string triggerSource, CancellationToken ct)
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
                    await TouchConnectionAnnotationAsync(ns, name, triggerSource, ct);
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
                await TouchConnectionByIdAsync(connectionId, triggerSource, ct);
            }
            else
            {
                _logger.LogWarning("Unknown connection key format: {ConnKey}", connKey);
            }
        }

        /// <summary>
        /// Finds and touches a Connection by its Auth0 ID (status.id).
        /// </summary>
        async Task TouchConnectionByIdAsync(string connectionId, string triggerSource, CancellationToken ct)
        {
            try
            {
                // List all connections and find the one with matching status.id
                var allConnections = await _kube.ListAsync<V1Connection>(@namespace: null, cancellationToken: ct);
                var connection = allConnections.FirstOrDefault(c => c.Status?.Id == connectionId);

                if (connection == null)
                {
                    _logger.LogDebug("Connection with Auth0 ID {ConnectionId} not found, skipping touch", connectionId);
                    return;
                }

                // Touch annotation to trigger reconciliation
                connection.Metadata.Annotations ??= new Dictionary<string, string>();
                connection.Metadata.Annotations["auth0.operator/last-client-change"] = DateTime.UtcNow.ToString("O");
                connection.Metadata.Annotations["auth0.operator/triggered-by"] = triggerSource;

                await _kube.UpdateAsync(connection, ct);
                _logger.LogInformation("Triggered Connection {Namespace}/{Name} (Auth0 ID: {ConnectionId}) reconciliation due to Client change: {Source}",
                    connection.Metadata.NamespaceProperty, connection.Metadata.Name, connectionId, triggerSource);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to touch Connection with Auth0 ID {ConnectionId}", connectionId);
            }
        }

        /// <summary>
        /// Touches a Connection's annotation to trigger reconciliation.
        /// </summary>
        /// <param name="ns"></param>
        /// <param name="name"></param>
        /// <param name="triggerSource"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        async Task TouchConnectionAnnotationAsync(string ns, string name, string triggerSource, CancellationToken ct)
        {
            try
            {
                var connection = await _kube.GetAsync<V1Connection>(name, ns, ct);
                if (connection == null)
                {
                    _logger.LogDebug("Connection {Namespace}/{Name} not found, skipping touch", ns, name);
                    return;
                }

                // Touch annotation to trigger reconciliation
                connection.Metadata.Annotations ??= new Dictionary<string, string>();
                connection.Metadata.Annotations["auth0.operator/last-client-change"] = DateTime.UtcNow.ToString("O");
                connection.Metadata.Annotations["auth0.operator/triggered-by"] = triggerSource;

                await _kube.UpdateAsync(connection, ct);
                _logger.LogInformation("Triggered Connection {Namespace}/{Name} reconciliation due to Client change: {Source}",
                    ns, name, triggerSource);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to touch Connection {Namespace}/{Name} annotation", ns, name);
            }
        }

    }

}
