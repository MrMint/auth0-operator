using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Polly;

namespace Alethic.Auth0.Operator.RateLimiting
{
    /// <summary>
    /// HTTP delegating handler that wraps requests with a Polly resilience pipeline.
    /// This handler applies retry, circuit breaker, and rate limit policies to all HTTP requests.
    /// </summary>
    public class ResilientHttpHandler : DelegatingHandler
    {
        private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;

        /// <summary>
        /// Initializes a new instance of the <see cref="ResilientHttpHandler"/> class.
        /// </summary>
        /// <param name="pipeline">The Polly resilience pipeline to apply to requests.</param>
        /// <param name="innerHandler">The inner HTTP handler to delegate to.</param>
        public ResilientHttpHandler(
            ResiliencePipeline<HttpResponseMessage> pipeline,
            HttpMessageHandler innerHandler)
        {
            _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
            InnerHandler = innerHandler ?? throw new ArgumentNullException(nameof(innerHandler));
        }

        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // Execute the request through the Polly resilience pipeline
            return await _pipeline.ExecuteAsync(
                async ct => await base.SendAsync(request, ct),
                cancellationToken);
        }
    }
}
