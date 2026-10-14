using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace Alethic.Auth0.Operator.RateLimiting
{
    /// <summary>
    /// Provides Polly resilience pipelines for Auth0 API calls.
    /// </summary>
    public interface IAuth0ResiliencePolicies
    {
        /// <summary>
        /// Gets the resilience pipeline for a specific tenant.
        /// </summary>
        /// <param name="tenantKey">The tenant identifier.</param>
        /// <returns>A resilience pipeline configured for the tenant.</returns>
        ResiliencePipeline<HttpResponseMessage> GetPipelineForTenant(string tenantKey);

        /// <summary>
        /// Gets the state of the circuit breaker for a tenant.
        /// </summary>
        /// <param name="tenantKey">The tenant identifier.</param>
        /// <returns>The current circuit breaker state, or null if no pipeline exists.</returns>
        CircuitState? GetCircuitState(string tenantKey);
    }

    /// <summary>
    /// Implementation of <see cref="IAuth0ResiliencePolicies"/> that creates
    /// per-tenant resilience pipelines with rate limit retry, transient retry,
    /// and circuit breaker policies.
    /// </summary>
    public class Auth0ResiliencePolicies : IAuth0ResiliencePolicies
    {
        private readonly ConcurrentDictionary<string, TenantPipeline> _pipelines = new();
        private readonly IRateLimiterService _rateLimiter;
        private readonly IOptions<RateLimitOptions> _options;
        private readonly ILogger<Auth0ResiliencePolicies> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="Auth0ResiliencePolicies"/> class.
        /// </summary>
        public Auth0ResiliencePolicies(
            IRateLimiterService rateLimiter,
            IOptions<RateLimitOptions> options,
            ILogger<Auth0ResiliencePolicies> logger)
        {
            _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public ResiliencePipeline<HttpResponseMessage> GetPipelineForTenant(string tenantKey)
        {
            var pipeline = _pipelines.GetOrAdd(tenantKey, CreatePipeline);
            return pipeline.Pipeline;
        }

        /// <inheritdoc />
        public CircuitState? GetCircuitState(string tenantKey)
        {
            return _pipelines.TryGetValue(tenantKey, out var pipeline)
                ? pipeline.CircuitBreakerState
                : null;
        }

        private TenantPipeline CreatePipeline(string tenantKey)
        {
            _logger.LogDebug("Creating resilience pipeline for tenant {TenantKey}", tenantKey);

            CircuitBreakerStateProvider? circuitStateProvider = null;

            var pipelineBuilder = new ResiliencePipelineBuilder<HttpResponseMessage>();

            // Layer 1: Rate limit retry with backoff from headers
            pipelineBuilder.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                Name = $"RateLimitRetry-{tenantKey}",
                MaxRetryAttempts = 3,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .HandleResult(response => response.StatusCode == HttpStatusCode.TooManyRequests),
                DelayGenerator = args =>
                {
                    var delay = TimeSpan.FromSeconds(_options.Value.MinRateLimitDelaySeconds);

                    // Try to extract Retry-After header
                    if (args.Outcome.Result?.Headers.RetryAfter?.Delta is { } retryAfter)
                    {
                        delay = retryAfter > delay ? retryAfter : delay;
                    }

                    // Report to rate limiter for state tracking
                    var resetAt = DateTimeOffset.UtcNow.Add(delay);
                    _rateLimiter.ReportRateLimitHit(tenantKey, resetAt);

                    _logger.LogWarning(
                        "Rate limit hit for tenant {TenantKey}, retrying after {DelaySeconds:F1}s (attempt {Attempt})",
                        tenantKey, delay.TotalSeconds, args.AttemptNumber + 1);

                    return ValueTask.FromResult<TimeSpan?>(delay);
                },
                OnRetry = args =>
                {
                    _logger.LogDebug(
                        "Rate limit retry {Attempt} for tenant {TenantKey}, delay: {DelaySeconds:F1}s",
                        args.AttemptNumber + 1, tenantKey, args.RetryDelay.TotalSeconds);
                    return default;
                }
            });

            // Layer 2: Transient error retry with exponential backoff
            pipelineBuilder.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                Name = $"TransientRetry-{tenantKey}",
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromSeconds(1),
                UseJitter = true,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .HandleResult(response => IsTransientError(response.StatusCode))
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutException>(),
                OnRetry = args =>
                {
                    _logger.LogWarning(
                        "Transient error for tenant {TenantKey}, retrying (attempt {Attempt}): {StatusCode}",
                        tenantKey, args.AttemptNumber + 1, args.Outcome.Result?.StatusCode);
                    return default;
                }
            });

            // Layer 3: Circuit breaker for repeated failures
            pipelineBuilder.AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                Name = $"CircuitBreaker-{tenantKey}",
                FailureRatio = 0.5,
                MinimumThroughput = 10,
                SamplingDuration = TimeSpan.FromMinutes(1),
                BreakDuration = TimeSpan.FromMinutes(1),
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .HandleResult(response => response.StatusCode == HttpStatusCode.TooManyRequests)
                    .HandleResult(response => IsTransientError(response.StatusCode))
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutException>(),
                StateProvider = circuitStateProvider = new CircuitBreakerStateProvider(),
                OnOpened = args =>
                {
                    _logger.LogWarning(
                        "Circuit breaker opened for tenant {TenantKey}, break duration: {BreakDuration}",
                        tenantKey, args.BreakDuration);
                    return default;
                },
                OnClosed = args =>
                {
                    _logger.LogInformation(
                        "Circuit breaker closed for tenant {TenantKey}",
                        tenantKey);
                    return default;
                },
                OnHalfOpened = args =>
                {
                    _logger.LogInformation(
                        "Circuit breaker half-opened for tenant {TenantKey}, testing recovery",
                        tenantKey);
                    return default;
                }
            });

            var pipeline = pipelineBuilder.Build();

            return new TenantPipeline(pipeline, circuitStateProvider);
        }

        private static bool IsTransientError(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.ServiceUnavailable
                   || statusCode == HttpStatusCode.GatewayTimeout
                   || statusCode == HttpStatusCode.BadGateway
                   || statusCode == HttpStatusCode.RequestTimeout;
        }

        /// <summary>
        /// Holds a tenant's pipeline and circuit breaker state provider.
        /// </summary>
        private sealed class TenantPipeline
        {
            public ResiliencePipeline<HttpResponseMessage> Pipeline { get; }
            private readonly CircuitBreakerStateProvider? _stateProvider;

            public CircuitState? CircuitBreakerState => _stateProvider?.CircuitState;

            public TenantPipeline(
                ResiliencePipeline<HttpResponseMessage> pipeline,
                CircuitBreakerStateProvider? stateProvider)
            {
                Pipeline = pipeline;
                _stateProvider = stateProvider;
            }
        }
    }
}
