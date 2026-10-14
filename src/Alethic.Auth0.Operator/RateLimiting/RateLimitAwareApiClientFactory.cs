using System;
using System.Net.Http;

using Auth0.ManagementApi;

namespace Alethic.Auth0.Operator.RateLimiting
{
    /// <summary>
    /// Factory that creates <see cref="IManagementApiClient"/> instances with rate limit tracking
    /// and Polly resilience policies.
    /// </summary>
    public class RateLimitAwareApiClientFactory : IManagementApiClientFactory
    {
        private readonly IRateLimiterService _rateLimiter;
        private readonly IAuth0ResiliencePolicies _resiliencePolicies;

        /// <summary>
        /// Initializes a new instance of the <see cref="RateLimitAwareApiClientFactory"/> class.
        /// </summary>
        /// <param name="rateLimiter">The rate limiter service for tracking API calls.</param>
        /// <param name="resiliencePolicies">The Polly resilience policies provider.</param>
        public RateLimitAwareApiClientFactory(
            IRateLimiterService rateLimiter,
            IAuth0ResiliencePolicies resiliencePolicies)
        {
            _rateLimiter = rateLimiter;
            _resiliencePolicies = resiliencePolicies;
        }

        /// <inheritdoc />
        public IManagementApiClient Create(string token, Uri baseUri, string tenantKey)
        {
            // Create HTTP handler chain with rate limit tracking
            var trackingHandler = new RateLimitTrackingHandler(_rateLimiter, tenantKey);

            // Wrap with Polly resilience handler
            var pipeline = _resiliencePolicies.GetPipelineForTenant(tenantKey);
            var outerHandler = new ResilientHttpHandler(pipeline, trackingHandler);

            // Create HTTP client with the handler chain
            var httpClient = new HttpClient(outerHandler)
            {
                BaseAddress = baseUri
            };

            // Create the management connection with our custom HTTP client
            var connection = new HttpClientManagementConnection(httpClient);

            // Create the management API client with our connection
            return new ManagementApiClient(token, baseUri, connection);
        }
    }

    /// <summary>
    /// Default factory that creates <see cref="IManagementApiClient"/> instances without rate limit tracking.
    /// Used when rate limiting is disabled.
    /// </summary>
    public class DefaultApiClientFactory : IManagementApiClientFactory
    {
        /// <inheritdoc />
        public IManagementApiClient Create(string token, Uri baseUri, string tenantKey)
        {
            return new ManagementApiClient(token, baseUri);
        }
    }
}
