using System;
using System.Threading;

namespace Alethic.Auth0.Operator.RateLimiting
{
    /// <summary>
    /// Tracks the rate limit state for a specific Auth0 tenant.
    /// Thread-safe implementation using Interlocked operations.
    /// </summary>
    public class TenantRateLimitState
    {
        private int _limit = 2;
        private int _remaining = 2;
        private long _resetAtTicks = DateTimeOffset.MinValue.UtcTicks;
        private long _lastUpdatedTicks = DateTimeOffset.MinValue.UtcTicks;
        private int _requestsSinceLastUpdate = 0;

        /// <summary>
        /// Tenant identifier (namespace/name).
        /// </summary>
        public string TenantKey { get; init; } = string.Empty;

        /// <summary>
        /// Maximum requests allowed in the current time window.
        /// </summary>
        public int Limit
        {
            get => Interlocked.CompareExchange(ref _limit, 0, 0);
            set => Interlocked.Exchange(ref _limit, value);
        }

        /// <summary>
        /// Remaining requests in current window.
        /// </summary>
        public int Remaining
        {
            get => Interlocked.CompareExchange(ref _remaining, 0, 0);
            set => Interlocked.Exchange(ref _remaining, value);
        }

        /// <summary>
        /// When the rate limit resets.
        /// </summary>
        public DateTimeOffset ResetAt
        {
            get => new DateTimeOffset(Interlocked.Read(ref _resetAtTicks), TimeSpan.Zero);
            set => Interlocked.Exchange(ref _resetAtTicks, value.UtcTicks);
        }

        /// <summary>
        /// Last time headers were updated from an API response.
        /// </summary>
        public DateTimeOffset LastUpdated
        {
            get => new DateTimeOffset(Interlocked.Read(ref _lastUpdatedTicks), TimeSpan.Zero);
            set => Interlocked.Exchange(ref _lastUpdatedTicks, value.UtcTicks);
        }

        /// <summary>
        /// Number of requests made since last header update.
        /// Used for pessimistic tracking when headers are stale.
        /// </summary>
        public int RequestsSinceLastUpdate
        {
            get => Interlocked.CompareExchange(ref _requestsSinceLastUpdate, 0, 0);
            set => Interlocked.Exchange(ref _requestsSinceLastUpdate, value);
        }

        /// <summary>
        /// Atomically increments RequestsSinceLastUpdate by the specified count.
        /// </summary>
        /// <param name="count">The number to add (default: 1).</param>
        /// <returns>The new value after incrementing.</returns>
        public int IncrementRequestsSinceLastUpdate(int count = 1)
        {
            return Interlocked.Add(ref _requestsSinceLastUpdate, count);
        }

        /// <summary>
        /// Estimated remaining requests (pessimistic calculation).
        /// </summary>
        public int EstimatedRemaining => Math.Max(0, Remaining - RequestsSinceLastUpdate);

        /// <summary>
        /// Whether we're approaching the rate limit (below 20% remaining).
        /// </summary>
        public bool IsApproachingLimit => Limit > 0 && EstimatedRemaining < (Limit * 0.2);

        /// <summary>
        /// Whether we should throttle requests (no quota left and reset is in the future).
        /// </summary>
        public bool ShouldThrottle => EstimatedRemaining <= 0 && ResetAt > DateTimeOffset.UtcNow;

        /// <summary>
        /// Time remaining until rate limit reset, or zero if already reset.
        /// </summary>
        public TimeSpan TimeUntilReset
        {
            get
            {
                var remaining = ResetAt - DateTimeOffset.UtcNow;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
    }
}
