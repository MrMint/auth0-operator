using System;
using System.Linq;

using Auth0.Core.Exceptions;

namespace Alethic.Auth0.Operator.RateLimiting
{
    /// <summary>
    /// Utility class for handling rate limit exceptions, including those wrapped in AggregateException.
    /// </summary>
    public static class RateLimitExceptionHandler
    {
        /// <summary>
        /// Attempts to extract a <see cref="RateLimitApiException"/> from an exception,
        /// handling <see cref="AggregateException"/> wrapping.
        /// </summary>
        /// <param name="exception">The exception to search.</param>
        /// <returns>The first <see cref="RateLimitApiException"/> found, or null if none exist.</returns>
        public static RateLimitApiException? ExtractRateLimitException(Exception exception)
        {
            return exception switch
            {
                RateLimitApiException rle => rle,
                AggregateException ae => ae.InnerExceptions
                    .Select(ExtractRateLimitException)
                    .FirstOrDefault(e => e != null),
                _ when exception.InnerException != null => ExtractRateLimitException(exception.InnerException),
                _ => null
            };
        }

        /// <summary>
        /// Checks if any exception in the tree is a <see cref="RateLimitApiException"/>.
        /// </summary>
        /// <param name="exception">The exception to check.</param>
        /// <returns>True if a rate limit exception is found.</returns>
        public static bool ContainsRateLimitException(Exception exception)
        {
            return ExtractRateLimitException(exception) != null;
        }

        /// <summary>
        /// Gets the reset time from a rate limit exception, with fallback.
        /// </summary>
        /// <param name="exception">The rate limit exception (may be null).</param>
        /// <param name="fallback">Fallback duration to use if no reset time is available.</param>
        /// <returns>The reset time from the exception, or current time plus fallback.</returns>
        public static DateTimeOffset GetResetTime(RateLimitApiException? exception, TimeSpan fallback)
        {
            if (exception?.RateLimit?.Reset is DateTimeOffset reset)
            {
                return reset;
            }
            return DateTimeOffset.UtcNow + fallback;
        }

        /// <summary>
        /// Gets the delay until the rate limit resets.
        /// </summary>
        /// <param name="exception">The rate limit exception.</param>
        /// <param name="minimumDelay">Minimum delay to return.</param>
        /// <returns>The delay until reset, or the minimum delay if reset time is in the past or unknown.</returns>
        public static TimeSpan GetDelayUntilReset(RateLimitApiException? exception, TimeSpan minimumDelay)
        {
            var resetTime = GetResetTime(exception, minimumDelay);
            var delay = resetTime - DateTimeOffset.UtcNow;

            return delay > minimumDelay ? delay : minimumDelay;
        }
    }
}
