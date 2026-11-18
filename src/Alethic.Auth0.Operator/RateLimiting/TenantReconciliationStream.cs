using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alethic.Auth0.Operator.RateLimiting
{
    /// <summary>
    /// Request to reconcile an entity.
    /// </summary>
    /// <param name="TenantKey">The tenant identifier (namespace/name).</param>
    /// <param name="EntityKey">The entity identifier (namespace/name).</param>
    /// <param name="EntityType">The type of entity being reconciled.</param>
    /// <param name="EnqueuedAt">When the request was enqueued.</param>
    /// <param name="Priority">Priority of this request (higher = more urgent).</param>
    public record ReconciliationRequest(
        string TenantKey,
        string EntityKey,
        string EntityType,
        DateTimeOffset EnqueuedAt,
        int Priority = 0);

    /// <summary>
    /// Event indicating a burst of rate limit hits was detected.
    /// </summary>
    /// <param name="TenantKey">The tenant identifier.</param>
    /// <param name="HitCount">Number of hits in the detection window.</param>
    /// <param name="Window">The time window over which hits were counted.</param>
    /// <param name="DetectedAt">When the burst was detected.</param>
    public record BurstDetected(
        string TenantKey,
        int HitCount,
        TimeSpan Window,
        DateTimeOffset DetectedAt);

    /// <summary>
    /// Manages per-tenant observable streams for rate-limited reconciliation processing.
    /// </summary>
    public interface ITenantReconciliationStreamManager : IDisposable
    {
        /// <summary>
        /// Warning threshold for high request volume per buffer window.
        /// Exceeding this logs a warning but does NOT drop requests.
        /// </summary>
        int MaxBufferSize { get; }

        /// <summary>
        /// Enqueues a reconciliation request for a tenant.
        /// </summary>
        /// <param name="request">The reconciliation request.</param>
        void EnqueueReconciliation(ReconciliationRequest request);

        /// <summary>
        /// Gets an observable of processed reconciliation requests for a tenant.
        /// </summary>
        /// <param name="tenantKey">The tenant identifier.</param>
        /// <returns>An observable of reconciliation requests.</returns>
        IObservable<ReconciliationRequest> GetStream(string tenantKey);

        /// <summary>
        /// Observable that emits when burst patterns are detected.
        /// </summary>
        IObservable<BurstDetected> BurstDetections { get; }
    }

    /// <summary>
    /// Implementation of <see cref="ITenantReconciliationStreamManager"/> that uses
    /// Rx.NET to provide per-tenant request tracking and burst detection.
    /// This is used for observability, not as the primary reconciliation control flow.
    /// </summary>
    public class TenantReconciliationStreamManager : ITenantReconciliationStreamManager
    {
        /// <summary>
        /// Default warning threshold for high request volume (100 requests per buffer window).
        /// </summary>
        public const int DefaultMaxBufferSize = 100;

        private readonly ConcurrentDictionary<string, TenantStream> _streams = new();
        private readonly ISubject<ReconciliationRequest> _allRequests;
        private readonly ISubject<BurstDetected> _burstDetections;
        private readonly IOptions<RateLimitOptions> _options;
        private readonly ILogger<TenantReconciliationStreamManager> _logger;
        private readonly CompositeDisposable _disposables = new();
        private bool _disposed;

        /// <inheritdoc />
        public int MaxBufferSize { get; }

        /// <inheritdoc />
        public IObservable<BurstDetected> BurstDetections => _burstDetections.AsObservable();

        /// <summary>
        /// Initializes a new instance of the <see cref="TenantReconciliationStreamManager"/> class.
        /// </summary>
        public TenantReconciliationStreamManager(
            IOptions<RateLimitOptions> options,
            ILogger<TenantReconciliationStreamManager> logger)
            : this(options, logger, DefaultMaxBufferSize)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TenantReconciliationStreamManager"/> class.
        /// </summary>
        /// <param name="options">Rate limit configuration options.</param>
        /// <param name="logger">Logger instance.</param>
        /// <param name="maxBufferSize">
        /// Warning threshold for high request volume per buffer window.
        /// When exceeded, a warning is logged but requests are NOT dropped.
        /// Note: Under sustained overload, backlog can grow without bound since we never drop.
        /// This is intentional for an infrastructure operator where data loss is unacceptable.
        /// </param>
        public TenantReconciliationStreamManager(
            IOptions<RateLimitOptions> options,
            ILogger<TenantReconciliationStreamManager> logger,
            int maxBufferSize)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            MaxBufferSize = maxBufferSize > 0 ? maxBufferSize : DefaultMaxBufferSize;

            // Use synchronized subjects for thread-safe OnNext from multiple controller threads
            _allRequests = Subject.Synchronize(new Subject<ReconciliationRequest>());
            _burstDetections = Subject.Synchronize(new Subject<BurstDetected>());

            // Set up burst detection across all tenants
            SetupBurstDetection();
        }

        /// <inheritdoc />
        public void EnqueueReconciliation(ReconciliationRequest request)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _allRequests.OnNext(request);

            var stream = _streams.GetOrAdd(request.TenantKey, CreateStream);
            stream.Enqueue(request);
        }

        /// <inheritdoc />
        public IObservable<ReconciliationRequest> GetStream(string tenantKey)
        {
            var stream = _streams.GetOrAdd(tenantKey, CreateStream);
            return stream.Requests;
        }

        private TenantStream CreateStream(string tenantKey)
        {
            // Use configured requests per minute, with conservative defaults based on Auth0 Self Service limits
            var requestsPerMinute = _options.Value.TargetRequestsPerMinute;
            if (requestsPerMinute <= 0)
            {
                requestsPerMinute = _options.Value.GetDefaultRequestsPerMinute();
            }

            _logger.LogDebug(
                "Creating reconciliation stream for {TenantKey} with {RPM} requests/minute limit and {MaxBuffer} max buffer",
                tenantKey, requestsPerMinute, MaxBufferSize);

            return new TenantStream(tenantKey, requestsPerMinute, MaxBufferSize, _logger);
        }

        private void SetupBurstDetection()
        {
            // Detect when many requests arrive for the same tenant in a short window
            var burstSubscription = _allRequests
                .GroupBy(r => r.TenantKey)
                .SelectMany(group =>
                    group
                        .Buffer(TimeSpan.FromSeconds(10))
                        .Where(batch => batch.Count >= 10)
                        .Select(batch => new BurstDetected(
                            group.Key,
                            batch.Count,
                            TimeSpan.FromSeconds(10),
                            DateTimeOffset.UtcNow)))
                .Subscribe(
                    burst =>
                    {
                        _logger.LogWarning(
                            "Burst detected for tenant {TenantKey}: {HitCount} requests in {Window}",
                            burst.TenantKey, burst.HitCount, burst.Window);
                        _burstDetections.OnNext(burst);
                    },
                    ex => _logger.LogError(ex, "Error in burst detection stream"));

            _disposables.Add(burstSubscription);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _allRequests.OnCompleted();
            _burstDetections.OnCompleted();
            _disposables.Dispose();

            foreach (var stream in _streams.Values)
            {
                stream.Dispose();
            }
            _streams.Clear();
        }

        /// <summary>
        /// Per-tenant stream that rate-limits requests with bounded buffering.
        /// </summary>
        private sealed class TenantStream : IDisposable
        {
            private readonly ISubject<ReconciliationRequest> _input;
            private readonly IConnectableObservable<ReconciliationRequest> _output;
            private readonly IDisposable _connection;

            /// <summary>
            /// Gets the hot observable of rate-limited reconciliation requests.
            /// </summary>
            public IObservable<ReconciliationRequest> Requests { get; }

            public TenantStream(string tenantKey, int requestsPerMinute, int maxBufferSize, ILogger logger)
            {

                // Use synchronized subject for thread-safe OnNext from multiple controller threads
                _input = Subject.Synchronize(new Subject<ReconciliationRequest>());

                // Rate-limit the stream with strict pacing based on requests per minute
                // Auth0 Self Service limits are typically 25-200 requests/minute per endpoint
                var intervalMs = 60000.0 / Math.Max(1, requestsPerMinute); // Convert RPM to interval
                var interval = TimeSpan.FromMilliseconds(intervalMs);
                var bufferWindow = TimeSpan.FromSeconds(5); // Buffer for 5 second windows (better batching at slower rates)

                // Use Select + Concat instead of SelectMany to serialize batches
                // This ensures strict RPS pacing - batches are processed sequentially,
                // not concurrently, preventing throughput from exceeding the intended rate
                //
                // NOTE: We intentionally do NOT drop requests here. This stream is used for
                // burst detection and observability, not as the primary reconciliation control.
                // KubeOps handles actual reconciliation. Dropping would cause silent data loss
                // which is unacceptable for an infrastructure operator.
                _output = _input
                    .Buffer(bufferWindow)
                    .Select(batch =>
                    {
                        if (batch.Count == 0)
                            return Observable.Empty<ReconciliationRequest>();

                        // Log warning if we're seeing high volume, but don't drop
                        if (batch.Count > maxBufferSize)
                        {
                            logger.LogWarning(
                                "Tenant {TenantKey} high request volume: {Count} requests in {Window}s window (threshold: {MaxBuffer}). " +
                                "Consider investigating burst patterns or increasing buffer size.",
                                tenantKey, batch.Count, bufferWindow.TotalSeconds, maxBufferSize);
                        }

                        // Process all requests - never drop for an infrastructure operator
                        return batch.ToObservable()
                            .Zip(Observable.Interval(interval).StartWith(0), (request, _) => request);
                    })
                    .Concat() // Serialize batch processing to maintain strict RPS limit
                    .Do(r => logger.LogDebug(
                        "Processing reconciliation request for {EntityKey} on tenant {TenantKey}",
                        r.EntityKey, r.TenantKey))
                    .Publish();

                // Connect immediately so the stream is hot
                _connection = _output.Connect();

                // Expose the hot published stream, not the cold pipeline
                Requests = _output.AsObservable();
            }

            public void Enqueue(ReconciliationRequest request)
            {
                _input.OnNext(request);
            }

            public void Dispose()
            {
                (_input as IDisposable)?.Dispose();
                _connection.Dispose();
            }
        }
    }
}
