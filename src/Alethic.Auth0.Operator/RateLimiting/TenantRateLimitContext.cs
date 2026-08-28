using System;
using System.Threading;

using Microsoft.Extensions.Logging;

namespace Alethic.Auth0.Operator.RateLimiting
{
    /// <summary>
    /// Implementation of <see cref="ITenantRateLimitContext"/> that tracks API operations
    /// during a single reconciliation scope.
    /// </summary>
    public class TenantRateLimitContext : ITenantRateLimitContext
    {
        private readonly IRateLimiterService _rateLimiter;
        private readonly ILogger<TenantRateLimitContext> _logger;
        private int _operationsStarted;
        private int _operationsCompleted;
        private bool _disposed;

        /// <inheritdoc />
        public string TenantKey { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="TenantRateLimitContext"/> class.
        /// </summary>
        public TenantRateLimitContext(
            string tenantKey,
            IRateLimiterService rateLimiter,
            ILogger<TenantRateLimitContext> logger)
        {
            TenantKey = tenantKey ?? throw new ArgumentNullException(nameof(tenantKey));
            _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public IApiOperationScope BeginApiOperation()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Interlocked.Increment(ref _operationsStarted);
            return new ApiOperationScope(this);
        }

        /// <inheritdoc />
        public TenantRateLimitSnapshot GetSnapshot()
        {
            var state = _rateLimiter.GetState(TenantKey);
            return new TenantRateLimitSnapshot(
                state?.Limit ?? 0,
                state?.Remaining ?? 0,
                state?.ResetAt ?? DateTimeOffset.MinValue,
                _operationsStarted,
                _operationsCompleted);
        }

        /// <summary>
        /// Called when an operation completes.
        /// </summary>
        internal void OnOperationCompleted()
        {
            Interlocked.Increment(ref _operationsCompleted);
        }

        /// <summary>
        /// Called when a rate limit is hit during an operation.
        /// </summary>
        internal void OnRateLimitHit(DateTimeOffset resetAt)
        {
            _rateLimiter.ReportRateLimitHit(TenantKey, resetAt);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _logger.LogDebug(
                "TenantRateLimitContext disposed for {TenantKey}: {Started} started, {Completed} completed",
                TenantKey, _operationsStarted, _operationsCompleted);
        }

        /// <summary>
        /// Scope that tracks a single API operation.
        /// </summary>
        private sealed class ApiOperationScope : IApiOperationScope
        {
            private readonly TenantRateLimitContext _context;
            private bool _completed;

            public ApiOperationScope(TenantRateLimitContext context)
            {
                _context = context;
            }

            public void Complete()
            {
                if (!_completed)
                {
                    _completed = true;
                    _context.OnOperationCompleted();
                }
            }

            public void ReportRateLimitHit(DateTimeOffset resetAt)
            {
                _context.OnRateLimitHit(resetAt);
            }

            public void Dispose()
            {
                // If not explicitly completed, still count as completed (may have failed)
                Complete();
            }
        }
    }

    /// <summary>
    /// Factory for creating <see cref="TenantRateLimitContext"/> instances.
    /// </summary>
    public class TenantRateLimitContextFactory : ITenantRateLimitContextFactory
    {
        private readonly IRateLimiterService _rateLimiter;
        private readonly ILogger<TenantRateLimitContext> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="TenantRateLimitContextFactory"/> class.
        /// </summary>
        public TenantRateLimitContextFactory(
            IRateLimiterService rateLimiter,
            ILogger<TenantRateLimitContext> logger)
        {
            _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public ITenantRateLimitContext Create(string tenantKey)
        {
            return new TenantRateLimitContext(tenantKey, _rateLimiter, _logger);
        }
    }
}
