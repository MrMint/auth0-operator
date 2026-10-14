using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;

using Microsoft.Extensions.Logging;

namespace Alethic.Auth0.Operator.RateLimiting
{
    /// <summary>
    /// Event indicating a rate limit was hit.
    /// </summary>
    /// <param name="TenantKey">The tenant identifier.</param>
    /// <param name="EntityKey">The entity that triggered the hit.</param>
    /// <param name="HitAt">When the rate limit was hit.</param>
    /// <param name="ResetAt">When the rate limit will reset.</param>
    /// <param name="RemainingBeforeHit">How many requests remained before this hit.</param>
    public record RateLimitHitEvent(
        string TenantKey,
        string EntityKey,
        DateTimeOffset HitAt,
        DateTimeOffset? ResetAt,
        int? RemainingBeforeHit);

    /// <summary>
    /// Recommendation to open a circuit breaker for a tenant.
    /// </summary>
    /// <param name="TenantKey">The tenant identifier.</param>
    /// <param name="HitsInWindow">Number of rate limit hits in the analysis window.</param>
    /// <param name="RecommendedBreakDuration">How long the circuit should stay open.</param>
    /// <param name="RecommendedUntil">When the circuit should try to close.</param>
    public record CircuitOpenRecommendation(
        string TenantKey,
        int HitsInWindow,
        TimeSpan RecommendedBreakDuration,
        DateTimeOffset RecommendedUntil);

    /// <summary>
    /// Observable stream of rate limit events for pattern analysis.
    /// </summary>
    public interface IRateLimitEventStream : IDisposable
    {
        /// <summary>
        /// Reports a rate limit hit event.
        /// </summary>
        /// <param name="hitEvent">The rate limit hit event.</param>
        void ReportHit(RateLimitHitEvent hitEvent);

        /// <summary>
        /// Observable of all rate limit hit events.
        /// </summary>
        IObservable<RateLimitHitEvent> Hits { get; }

        /// <summary>
        /// Observable of tenants that should have their circuit opened.
        /// Emits when repeated rate limits are detected within a time window.
        /// </summary>
        IObservable<CircuitOpenRecommendation> CircuitRecommendations { get; }
    }

    /// <summary>
    /// Implementation of <see cref="IRateLimitEventStream"/> that analyzes
    /// rate limit hit patterns and recommends circuit breaker actions.
    /// </summary>
    public class RateLimitEventStream : IRateLimitEventStream
    {
        private readonly ISubject<RateLimitHitEvent> _hits;
        private readonly CompositeDisposable _disposables = new();
        private readonly ILogger<RateLimitEventStream> _logger;
        private bool _disposed;

        /// <inheritdoc />
        public IObservable<RateLimitHitEvent> Hits => _hits.AsObservable();

        /// <inheritdoc />
        public IObservable<CircuitOpenRecommendation> CircuitRecommendations { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="RateLimitEventStream"/> class.
        /// </summary>
        public RateLimitEventStream(ILogger<RateLimitEventStream> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // Use synchronized subject for thread-safe OnNext from multiple controller threads
            _hits = Subject.Synchronize(new Subject<RateLimitHitEvent>());

            // Analyze hit patterns per tenant to recommend circuit opening
            // If we see 3+ rate limit hits within 30 seconds for a tenant,
            // recommend opening the circuit
            CircuitRecommendations = _hits
                .GroupBy(h => h.TenantKey)
                .SelectMany(group =>
                    group
                        .Buffer(TimeSpan.FromSeconds(30))
                        .Where(batch => batch.Count >= 3)
                        .Select(batch =>
                        {
                            var breakDuration = TimeSpan.FromMinutes(1);
                            return new CircuitOpenRecommendation(
                                group.Key,
                                batch.Count,
                                breakDuration,
                                DateTimeOffset.UtcNow.Add(breakDuration));
                        }));

            // Subscribe to log recommendations
            var subscription = CircuitRecommendations.Subscribe(
                recommendation => _logger.LogWarning(
                    "Circuit open recommended for tenant {TenantKey}: {HitsInWindow} hits in 30s, recommend break until {RecommendedUntil}",
                    recommendation.TenantKey, recommendation.HitsInWindow, recommendation.RecommendedUntil),
                ex => _logger.LogError(ex, "Error in circuit recommendation stream"));

            _disposables.Add(subscription);
        }

        /// <inheritdoc />
        public void ReportHit(RateLimitHitEvent hitEvent)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _logger.LogDebug(
                "Rate limit hit for {TenantKey}/{EntityKey} at {HitAt}",
                hitEvent.TenantKey, hitEvent.EntityKey, hitEvent.HitAt);

            _hits.OnNext(hitEvent);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _hits.OnCompleted();
            (_hits as IDisposable)?.Dispose();
            _disposables.Dispose();
        }
    }
}
