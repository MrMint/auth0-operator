using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Alethic.Auth0.Operator.RateLimiting
{
    /// <summary>
    /// HTTP delegating handler that captures rate limit headers from Auth0 API responses
    /// and reports them to the rate limiter service.
    /// </summary>
    public class RateLimitTrackingHandler : DelegatingHandler
    {
        private readonly IRateLimiterService _rateLimiter;
        private readonly string _tenantKey;

        /// <summary>
        /// Initializes a new instance of the <see cref="RateLimitTrackingHandler"/> class.
        /// </summary>
        /// <param name="rateLimiter">The rate limiter service to report to.</param>
        /// <param name="tenantKey">The tenant identifier for this handler.</param>
        /// <param name="innerHandler">The inner HTTP handler (optional).</param>
        public RateLimitTrackingHandler(
            IRateLimiterService rateLimiter,
            string tenantKey,
            HttpMessageHandler? innerHandler = null)
        {
            _rateLimiter = rateLimiter;
            _tenantKey = tenantKey;
            InnerHandler = innerHandler ?? new HttpClientHandler();
        }

        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);

            // Extract rate limit headers from response
            ExtractAndReportRateLimitHeaders(response);

            return response;
        }

        /// <summary>
        /// Extracts rate limit headers from the response and reports them to the rate limiter.
        /// </summary>
        private void ExtractAndReportRateLimitHeaders(HttpResponseMessage response)
        {
            int? limit = null;
            int? remaining = null;
            DateTimeOffset? resetAt = null;

            // Extract X-RateLimit-Limit
            if (response.Headers.TryGetValues("X-RateLimit-Limit", out var limitValues))
            {
                var limitStr = limitValues.FirstOrDefault();
                if (int.TryParse(limitStr, out var l))
                    limit = l;
            }

            // Extract X-RateLimit-Remaining
            if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues))
            {
                var remainingStr = remainingValues.FirstOrDefault();
                if (int.TryParse(remainingStr, out var r))
                    remaining = r;
            }

            // Extract X-RateLimit-Reset (Unix timestamp)
            if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues))
            {
                var resetStr = resetValues.FirstOrDefault();
                if (long.TryParse(resetStr, out var epoch))
                    resetAt = DateTimeOffset.FromUnixTimeSeconds(epoch);
            }

            // Update rate limiter with latest headers
            if (limit.HasValue || remaining.HasValue || resetAt.HasValue)
            {
                _rateLimiter.UpdateFromHeaders(_tenantKey, limit, remaining, resetAt);
            }

            // Handle 429 Too Many Requests
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var resetTime = resetAt ?? DateTimeOffset.UtcNow.AddMinutes(1);
                _rateLimiter.ReportRateLimitHit(_tenantKey, resetTime);
            }
        }
    }
}
