using System;
using System.Threading;
using System.Threading.Tasks;

namespace Alethic.Auth0.Operator.RateLimiting
{
    /// <summary>
    /// Service for managing rate limits across Auth0 tenants.
    /// </summary>
    public interface IRateLimiterService
    {
        /// <summary>
        /// Acquires permission to make an API call for the specified tenant.
        /// Returns immediately if quota available, or waits/returns retry info if throttled.
        /// </summary>
        /// <param name="tenantKey">The tenant identifier (namespace/name).</param>
        /// <param name="estimatedCalls">Number of API calls expected for this operation.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Acquisition result indicating whether the request can proceed.</returns>
        Task<RateLimitAcquisition> AcquireAsync(
            string tenantKey,
            int estimatedCalls = 1,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates rate limit state from API response headers.
        /// Should be called after every Auth0 API call.
        /// </summary>
        /// <param name="tenantKey">The tenant identifier.</param>
        /// <param name="limit">X-RateLimit-Limit header value.</param>
        /// <param name="remaining">X-RateLimit-Remaining header value.</param>
        /// <param name="resetAt">X-RateLimit-Reset header value (Unix timestamp converted to DateTimeOffset).</param>
        void UpdateFromHeaders(string tenantKey, int? limit, int? remaining, DateTimeOffset? resetAt);

        /// <summary>
        /// Reports that a rate limit was hit for immediate throttling.
        /// Should be called when a 429 response is received.
        /// </summary>
        /// <param name="tenantKey">The tenant identifier.</param>
        /// <param name="resetAt">When the rate limit will reset.</param>
        void ReportRateLimitHit(string tenantKey, DateTimeOffset resetAt);

        /// <summary>
        /// Gets the current rate limit state for a tenant (for diagnostics/metrics).
        /// </summary>
        /// <param name="tenantKey">The tenant identifier.</param>
        /// <returns>The current state, or null if no state exists.</returns>
        TenantRateLimitState? GetState(string tenantKey);

        /// <summary>
        /// Gets recommended delay before next reconciliation for a tenant.
        /// Returns TimeSpan.Zero if no additional delay is needed.
        /// </summary>
        /// <param name="tenantKey">The tenant identifier.</param>
        /// <returns>Recommended delay to add to reconciliation interval.</returns>
        TimeSpan GetRecommendedDelay(string tenantKey);
    }

    /// <summary>
    /// Result of attempting to acquire permission for an API call.
    /// </summary>
    /// <param name="Acquired">Whether the request can proceed.</param>
    /// <param name="RetryAfter">If not acquired, how long to wait before retrying.</param>
    /// <param name="TenantKey">The tenant this acquisition is for.</param>
    public record RateLimitAcquisition(
        bool Acquired,
        TimeSpan? RetryAfter,
        string TenantKey
    );
}
