using System;

namespace Alethic.Auth0.Operator.RateLimiting
{
    /// <summary>
    /// Configuration options for rate limiting behavior.
    /// Based on Auth0 Self Service Management API limits:
    /// - Most write operations: burst 5-20, sustained 25-200/minute
    /// - Most read operations: burst 5-40, sustained 50-500/minute
    /// See: https://auth0.com/docs/troubleshoot/customer-support/operational-policies/rate-limit-policy/rate-limit-configurations/self-service-public
    /// </summary>
    public class RateLimitOptions
    {
        /// <summary>
        /// Enable proactive throttling based on X-RateLimit headers.
        /// When enabled, the operator will slow down when approaching rate limits.
        /// </summary>
        public bool EnableProactiveThrottling { get; set; } = true;

        /// <summary>
        /// Reconciliation interval when approaching rate limits (seconds).
        /// Default: 300 seconds (5 minutes) - backs off harder when under pressure.
        /// </summary>
        public int ThrottledReconciliationIntervalSeconds { get; set; } = 300;

        /// <summary>
        /// Minimum delay after hitting rate limit (seconds).
        /// Default: 60 seconds - aligns with Auth0's typical 1-minute rate limit windows.
        /// </summary>
        public int MinRateLimitDelaySeconds { get; set; } = 60;

        /// <summary>
        /// Default tier assumption for new tenants.
        /// Affects initial rate limit estimates before first API response.
        /// </summary>
        public TenantTier DefaultTier { get; set; } = TenantTier.SelfService;

        /// <summary>
        /// Target requests per minute for the Rx-based pacing stream.
        /// This is a conservative default based on Auth0 Self Service limits.
        /// Most Management API endpoints allow 25-200 requests/minute sustained.
        /// Default: 30/minute (0.5 RPS) - conservative to stay well under limits.
        /// </summary>
        public int TargetRequestsPerMinute { get; set; } = 30;

        /// <summary>
        /// Gets the default rate limit (requests per minute) based on the configured tier.
        /// These values are conservative estimates based on Auth0 Self Service limits.
        /// </summary>
        public int GetDefaultRequestsPerMinute() => DefaultTier switch
        {
            TenantTier.SelfService => 30,  // Conservative: most endpoints allow 50-200/min
            TenantTier.Enterprise => 60,   // Enterprise has higher limits
            _ => 30
        };
    }

    /// <summary>
    /// Auth0 tenant subscription tier.
    /// </summary>
    public enum TenantTier
    {
        /// <summary>
        /// Self Service tier (formerly Free/Paid distinction).
        /// Management API limits: typically 25-200 requests/minute sustained per endpoint.
        /// Burst limits: typically 5-40 requests.
        /// </summary>
        SelfService,

        /// <summary>
        /// Enterprise tier with higher limits.
        /// Contact Auth0 for specific limits.
        /// </summary>
        Enterprise
    }
}
