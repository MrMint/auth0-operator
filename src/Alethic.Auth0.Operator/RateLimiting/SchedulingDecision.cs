using System;

namespace Alethic.Auth0.Operator.RateLimiting
{
    /// <summary>
    /// Result of a scheduling decision indicating whether reconciliation should proceed.
    /// </summary>
    /// <param name="ShouldProceed">Whether the reconciliation should proceed immediately.</param>
    /// <param name="Delay">The delay to wait before retrying, if not proceeding.</param>
    /// <param name="Reason">The reason for the decision.</param>
    public record SchedulingDecision(
        bool ShouldProceed,
        TimeSpan? Delay,
        SchedulingReason Reason)
    {
        /// <summary>
        /// Creates a decision indicating that reconciliation should proceed.
        /// </summary>
        public static SchedulingDecision Proceed() =>
            new(true, null, SchedulingReason.Proceed);

        /// <summary>
        /// Creates a decision indicating that reconciliation should be deferred.
        /// </summary>
        /// <param name="delay">The delay before the next attempt.</param>
        /// <param name="reason">The reason for deferral.</param>
        public static SchedulingDecision Defer(TimeSpan delay, SchedulingReason reason) =>
            new(false, delay, reason);
    }

    /// <summary>
    /// Reasons for scheduling decisions.
    /// </summary>
    public enum SchedulingReason
    {
        /// <summary>
        /// Reconciliation should proceed.
        /// </summary>
        Proceed,

        /// <summary>
        /// Deferred due to startup spread to prevent thundering herd.
        /// </summary>
        StartupSpread,

        /// <summary>
        /// Deferred due to proactive throttling when approaching rate limits.
        /// </summary>
        ProactiveThrottle,

        /// <summary>
        /// Deferred due to rate limit backoff after hitting 429.
        /// </summary>
        RateLimitBackoff,

        /// <summary>
        /// Deferred due to circuit breaker being open.
        /// </summary>
        CircuitOpen
    }
}
