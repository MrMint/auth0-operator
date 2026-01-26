using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Alethic.Auth0.Operator.Options;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alethic.Auth0.Operator.RateLimiting
{
    /// <summary>
    /// Implementation of <see cref="IReconciliationScheduler"/> that centralizes
    /// all requeue/delay decisions using IMemoryCache for startup spread tracking.
    /// </summary>
    public class ReconciliationScheduler : IReconciliationScheduler, IDisposable
    {
        private readonly IMemoryCache _cache;
        private readonly IRateLimiterService _rateLimiter;
        private readonly IOptions<OperatorOptions> _options;
        private readonly IRateLimitEventStream? _eventStream;
        private readonly ITenantReconciliationStreamManager? _streamManager;
        private readonly ILogger<ReconciliationScheduler> _logger;
        private readonly ConcurrentDictionary<string, DateTimeOffset> _circuitOpenUntil = new();
        private readonly IDisposable? _circuitSubscription;
        private readonly IDisposable? _burstSubscription;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="ReconciliationScheduler"/> class.
        /// </summary>
        public ReconciliationScheduler(
            IMemoryCache cache,
            IRateLimiterService rateLimiter,
            IOptions<OperatorOptions> options,
            ILogger<ReconciliationScheduler> logger,
            IRateLimitEventStream? eventStream = null,
            ITenantReconciliationStreamManager? streamManager = null)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventStream = eventStream;
            _streamManager = streamManager;

            // Subscribe to circuit open recommendations from the Rx stream
            if (_eventStream != null)
            {
                _circuitSubscription = _eventStream.CircuitRecommendations.Subscribe(
                    OnCircuitOpenRecommendation,
                    ex => _logger.LogError(ex, "Error in circuit recommendation subscription"));
            }

            // Subscribe to burst detections from the stream manager
            if (_streamManager != null)
            {
                _burstSubscription = _streamManager.BurstDetections.Subscribe(
                    OnBurstDetected,
                    ex => _logger.LogError(ex, "Error in burst detection subscription"));
            }
        }

        /// <summary>
        /// Handles burst detections from the stream manager.
        /// </summary>
        private void OnBurstDetected(BurstDetected burst)
        {
            _logger.LogWarning(
                "Burst detected for tenant {TenantKey}: {HitCount} requests in {Window}",
                burst.TenantKey, burst.HitCount, burst.Window);

            // Open circuit for this tenant to let rate limits recover
            var cooldownDuration = TimeSpan.FromSeconds(30);
            var openUntil = DateTimeOffset.UtcNow.Add(cooldownDuration);
            _circuitOpenUntil[burst.TenantKey] = openUntil;
        }

        /// <summary>
        /// Handles circuit open recommendations from the Rx event stream.
        /// </summary>
        private void OnCircuitOpenRecommendation(CircuitOpenRecommendation recommendation)
        {
            _logger.LogWarning(
                "Circuit open for tenant {TenantKey} due to {HitsInWindow} rate limit hits, until {RecommendedUntil}",
                recommendation.TenantKey, recommendation.HitsInWindow, recommendation.RecommendedUntil);

            _circuitOpenUntil[recommendation.TenantKey] = recommendation.RecommendedUntil;
        }

        /// <summary>
        /// Checks if the circuit is open for a tenant.
        /// </summary>
        private bool IsCircuitOpen(string tenantKey, out TimeSpan delay)
        {
            delay = TimeSpan.Zero;

            if (!_circuitOpenUntil.TryGetValue(tenantKey, out var openUntil))
                return false;

            var now = DateTimeOffset.UtcNow;
            if (openUntil <= now)
            {
                // Circuit has closed, remove the entry
                _circuitOpenUntil.TryRemove(tenantKey, out _);
                return false;
            }

            delay = openUntil - now;
            return true;
        }

        /// <inheritdoc />
        public async Task<SchedulingDecision> BeginReconcileAsync(
            string entityKey,
            string tenantKey,
            int estimatedCalls = 1,
            CancellationToken cancellationToken = default)
        {
            var reconciliationOptions = _options.Value.Reconciliation;

            // 1. Check Rx-based circuit breaker
            if (IsCircuitOpen(tenantKey, out var circuitDelay))
            {
                _logger.LogInformation(
                    "Circuit open for tenant {TenantKey}, deferring entity {EntityKey} for {DelaySeconds:F1}s",
                    tenantKey, entityKey, circuitDelay.TotalSeconds);

                return SchedulingDecision.Defer(circuitDelay, SchedulingReason.CircuitOpen);
            }

            // 2. Check startup spread (first reconciliation of this entity)
            if (reconciliationOptions.EnableStartupSpread && !HasReconciledSinceStartup(entityKey))
            {
                // Always mark as reconciled on first touch to prevent re-checking
                MarkReconciled(entityKey);
                
                var delay = ComputeStartupSpreadDelay(entityKey);
                if (delay > TimeSpan.Zero)
                {
                    _logger.LogDebug(
                        "Applying startup spread delay of {DelayMs}ms for entity {EntityKey}",
                        delay.TotalMilliseconds, entityKey);

                    return SchedulingDecision.Defer(delay, SchedulingReason.StartupSpread);
                }
                // If delay is 0, fall through to proceed immediately
            }

            // 3. Check proactive throttling (approaching rate limits)
            var acquisition = await _rateLimiter.AcquireAsync(tenantKey, estimatedCalls, cancellationToken);
            if (!acquisition.Acquired && acquisition.RetryAfter.HasValue)
            {
                var minDelay = reconciliationOptions.MinThrottledInterval;
                var actualDelay = acquisition.RetryAfter.Value > minDelay 
                    ? acquisition.RetryAfter.Value 
                    : minDelay;

                _logger.LogInformation(
                    "Proactive throttling for entity {EntityKey} on tenant {TenantKey}, delay: {DelaySeconds:F1}s",
                    entityKey, tenantKey, actualDelay.TotalSeconds);

                return SchedulingDecision.Defer(actualDelay, SchedulingReason.ProactiveThrottle);
            }

            // 4. All checks passed - proceed with reconciliation
            MarkReconciled(entityKey);
            
            // Track this reconciliation request in the stream manager for burst detection
            _streamManager?.EnqueueReconciliation(new ReconciliationRequest(
                TenantKey: tenantKey,
                EntityKey: entityKey,
                EntityType: "unknown", // We don't have the entity type here
                EnqueuedAt: DateTimeOffset.UtcNow));
            
            return SchedulingDecision.Proceed();
        }

        /// <inheritdoc />
        public bool HasReconciledSinceStartup(string entityKey)
        {
            return _cache.TryGetValue(CacheKey(entityKey), out _);
        }

        /// <inheritdoc />
        public void EndReconcile(string entityKey, bool success)
        {
            _logger.LogDebug(
                "Reconciliation completed for {EntityKey}, success: {Success}",
                entityKey, success);
        }

        /// <summary>
        /// Marks an entity as reconciled in the cache with sliding expiration.
        /// </summary>
        private void MarkReconciled(string entityKey)
        {
            var options = new MemoryCacheEntryOptions()
                .SetSlidingExpiration(TimeSpan.FromHours(1))
                .SetAbsoluteExpiration(TimeSpan.FromHours(24));

            _cache.Set(CacheKey(entityKey), DateTimeOffset.UtcNow, options);
        }

        /// <summary>
        /// Computes the startup spread delay for an entity based on its key.
        /// Uses a stable hash to ensure the same entity always gets the same delay.
        /// </summary>
        private TimeSpan ComputeStartupSpreadDelay(string entityKey)
        {
            var spreadWindow = _options.Value.Reconciliation.StartupSpreadWindow;
            var maxSpreadMs = (int)spreadWindow.TotalMilliseconds;
            if (maxSpreadMs <= 0) return TimeSpan.Zero;

            var hash = ComputeStableHash(entityKey);
            return TimeSpan.FromMilliseconds(hash % maxSpreadMs);
        }

        /// <summary>
        /// Computes a stable, non-negative hash for the given key using SHA256.
        /// </summary>
        private static int ComputeStableHash(string key)
        {
            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            return BitConverter.ToInt32(hashBytes, 0) & int.MaxValue;
        }

        /// <summary>
        /// Generates the cache key for an entity.
        /// </summary>
        private static string CacheKey(string entityKey) => $"reconciliation:reconciled:{entityKey}";

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _circuitSubscription?.Dispose();
            _burstSubscription?.Dispose();
            _circuitOpenUntil.Clear();
        }
    }
}
