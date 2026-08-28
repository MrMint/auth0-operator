using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alethic.Auth0.Operator.RateLimiting
{
    /// <summary>
    /// Implementation of <see cref="IRateLimiterService"/> that tracks rate limit state
    /// per tenant and provides proactive throttling.
    /// </summary>
    public class RateLimiterService : IRateLimiterService, IDisposable
    {
        private readonly ConcurrentDictionary<string, TenantRateLimitState> _states = new();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
        private readonly ILogger<RateLimiterService> _logger;
        private readonly RateLimitOptions _options;
        private readonly IRateLimitEventStream? _eventStream;
        private readonly Timer _cleanupTimer;
        private readonly TimeSpan _staleEntryThreshold = TimeSpan.FromHours(1);
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="RateLimiterService"/> class.
        /// </summary>
        public RateLimiterService(
            ILogger<RateLimiterService> logger,
            IOptions<RateLimitOptions> options,
            IRateLimitEventStream? eventStream = null)
        {
            _logger = logger;
            _options = options.Value;
            _eventStream = eventStream;

            // Run cleanup every 15 minutes to remove stale entries
            _cleanupTimer = new Timer(
                CleanupStaleEntries,
                null,
                TimeSpan.FromMinutes(15),
                TimeSpan.FromMinutes(15)
            );
        }

        /// <inheritdoc />
        public async Task<RateLimitAcquisition> AcquireAsync(
            string tenantKey,
            int estimatedCalls = 1,
            CancellationToken cancellationToken = default)
        {
            var state = GetOrCreateState(tenantKey);
            var semaphore = _locks.GetOrAdd(tenantKey, _ => new SemaphoreSlim(1, 1));

            await semaphore.WaitAsync(cancellationToken);
            try
            {
                // Check if we should wait for reset
                if (state.ShouldThrottle && _options.EnableProactiveThrottling)
                {
                    var delay = state.TimeUntilReset;
                    if (delay > TimeSpan.Zero)
                    {
                        _logger.LogInformation(
                            "Rate limit throttling for tenant {TenantKey}, waiting {DelaySeconds:F1}s until reset",
                            tenantKey, delay.TotalSeconds);

                        return new RateLimitAcquisition(false, delay, tenantKey);
                    }
                }

                // Track pessimistic usage using atomic increment
                state.IncrementRequestsSinceLastUpdate(estimatedCalls);

                return new RateLimitAcquisition(true, null, tenantKey);
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <inheritdoc />
        public void UpdateFromHeaders(string tenantKey, int? limit, int? remaining, DateTimeOffset? resetAt)
        {
            var state = GetOrCreateState(tenantKey);

            if (limit.HasValue)
                state.Limit = limit.Value;

            if (remaining.HasValue)
                state.Remaining = remaining.Value;

            if (resetAt.HasValue)
                state.ResetAt = resetAt.Value;

            // Reset pessimistic counter since we have fresh data
            state.RequestsSinceLastUpdate = 0;
            state.LastUpdated = DateTimeOffset.UtcNow;

            _logger.LogDebug(
                "Updated rate limit state for {TenantKey}: {Remaining}/{Limit} remaining, resets at {ResetAt}",
                tenantKey, state.Remaining, state.Limit, state.ResetAt);
        }

        /// <inheritdoc />
        public void ReportRateLimitHit(string tenantKey, DateTimeOffset resetAt)
        {
            ReportRateLimitHit(tenantKey, resetAt, entityKey: null, remainingBeforeHit: null);
        }

        /// <summary>
        /// Reports a rate limit hit with additional context for Rx event stream.
        /// </summary>
        /// <param name="tenantKey">The tenant identifier.</param>
        /// <param name="resetAt">When the rate limit will reset.</param>
        /// <param name="entityKey">The entity that triggered the hit (optional).</param>
        /// <param name="remainingBeforeHit">Remaining requests before this hit (optional).</param>
        public void ReportRateLimitHit(string tenantKey, DateTimeOffset resetAt, string? entityKey, int? remainingBeforeHit)
        {
            var state = GetOrCreateState(tenantKey);
            state.Remaining = 0;
            state.ResetAt = resetAt;
            state.RequestsSinceLastUpdate = 0;
            state.LastUpdated = DateTimeOffset.UtcNow;

            _logger.LogWarning(
                "Rate limit hit reported for {TenantKey}, throttling until {ResetAt}",
                tenantKey, resetAt);

            // Publish to Rx event stream
            if (_eventStream != null)
            {
                var hitEvent = new RateLimitHitEvent(
                    TenantKey: tenantKey,
                    EntityKey: entityKey ?? "unknown",
                    HitAt: DateTimeOffset.UtcNow,
                    ResetAt: resetAt,
                    RemainingBeforeHit: remainingBeforeHit);
                _eventStream.ReportHit(hitEvent);
            }
        }

        /// <inheritdoc />
        public TenantRateLimitState? GetState(string tenantKey)
        {
            return _states.TryGetValue(tenantKey, out var state) ? state : null;
        }

        /// <inheritdoc />
        public TimeSpan GetRecommendedDelay(string tenantKey)
        {
            var state = GetState(tenantKey);
            if (state == null)
                return TimeSpan.Zero;

            // If throttled, wait until reset
            if (state.ShouldThrottle)
            {
                var delay = state.TimeUntilReset;
                if (delay > TimeSpan.Zero)
                    return delay;
            }

            // Slow down reconciliation when approaching limits
            if (state.IsApproachingLimit && _options.EnableProactiveThrottling)
            {
                return TimeSpan.FromSeconds(_options.ThrottledReconciliationIntervalSeconds);
            }

            return TimeSpan.Zero;
        }

        /// <summary>
        /// Gets or creates the rate limit state for a tenant.
        /// </summary>
        private TenantRateLimitState GetOrCreateState(string tenantKey)
        {
            var defaultRpm = _options.GetDefaultRequestsPerMinute();
            return _states.GetOrAdd(tenantKey, key => new TenantRateLimitState
            {
                TenantKey = key,
                Limit = defaultRpm,
                Remaining = defaultRpm
            });
        }

        /// <summary>
        /// Periodically cleans up stale entries from the state and locks dictionaries.
        /// Only removes entries that have been updated at least once and are now stale.
        /// </summary>
        private void CleanupStaleEntries(object? state)
        {
            if (_disposed)
                return;

            var threshold = DateTimeOffset.UtcNow - _staleEntryThreshold;
            var keysToRemove = new List<string>();

            foreach (var kvp in _states)
            {
                // Only consider entries that have actually been updated from API responses.
                // Entries with LastUpdated at MinValue have never received headers and may
                // still be active - don't evict them as it causes unnecessary churn.
                if (kvp.Value.LastUpdated > DateTimeOffset.MinValue && kvp.Value.LastUpdated < threshold)
                {
                    keysToRemove.Add(kvp.Key);
                }
            }

            foreach (var key in keysToRemove)
            {
                if (_states.TryRemove(key, out _))
                {
                    if (_locks.TryRemove(key, out var semaphore))
                    {
                        semaphore.Dispose();
                    }
                    _logger.LogDebug("Cleaned up stale rate limit state for tenant {TenantKey}", key);
                }
            }

            if (keysToRemove.Count > 0)
            {
                _logger.LogInformation("Cleaned up {Count} stale rate limit entries", keysToRemove.Count);
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _cleanupTimer.Dispose();

            foreach (var semaphore in _locks.Values)
            {
                semaphore.Dispose();
            }

            _locks.Clear();
            _states.Clear();
        }
    }
}
