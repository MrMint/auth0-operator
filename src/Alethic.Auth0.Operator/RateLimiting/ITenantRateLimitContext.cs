using System;

namespace Alethic.Auth0.Operator.RateLimiting
{
    /// <summary>
    /// Scoped context for rate limit operations during a single reconciliation.
    /// Tracks API operations and provides access to rate limit state.
    /// </summary>
    public interface ITenantRateLimitContext : IDisposable
    {
        /// <summary>
        /// The tenant key (namespace/name) for this context.
        /// </summary>
        string TenantKey { get; }

        /// <summary>
        /// Begins tracking an API operation. Returns a scope that records completion.
        /// </summary>
        /// <returns>A scope that should be disposed when the operation completes.</returns>
        IApiOperationScope BeginApiOperation();

        /// <summary>
        /// Gets a snapshot of current rate limit state.
        /// </summary>
        /// <returns>A snapshot of the current rate limit state.</returns>
        TenantRateLimitSnapshot GetSnapshot();
    }

    /// <summary>
    /// Scope for tracking a single API operation within a reconciliation.
    /// </summary>
    public interface IApiOperationScope : IDisposable
    {
        /// <summary>
        /// Marks the operation as completed successfully.
        /// </summary>
        void Complete();

        /// <summary>
        /// Reports that a rate limit was hit during this operation.
        /// </summary>
        /// <param name="resetAt">When the rate limit will reset.</param>
        void ReportRateLimitHit(DateTimeOffset resetAt);
    }

    /// <summary>
    /// Factory for creating tenant rate limit contexts.
    /// </summary>
    public interface ITenantRateLimitContextFactory
    {
        /// <summary>
        /// Creates a new rate limit context for the specified tenant.
        /// </summary>
        /// <param name="tenantKey">The tenant key (namespace/name).</param>
        /// <returns>A new tenant rate limit context.</returns>
        ITenantRateLimitContext Create(string tenantKey);
    }

    /// <summary>
    /// Snapshot of rate limit state at a point in time.
    /// </summary>
    /// <param name="Limit">Maximum requests allowed in the current window.</param>
    /// <param name="Remaining">Remaining requests in the current window.</param>
    /// <param name="ResetAt">When the rate limit window resets.</param>
    /// <param name="OperationsStarted">Number of API operations started in this context.</param>
    /// <param name="OperationsCompleted">Number of API operations completed in this context.</param>
    public record TenantRateLimitSnapshot(
        int Limit,
        int Remaining,
        DateTimeOffset ResetAt,
        int OperationsStarted,
        int OperationsCompleted);
}
