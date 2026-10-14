using System;

namespace Alethic.Auth0.Operator.Options
{

    /// <summary>
    /// Configuration for reconciliation behavior.
    /// </summary>
    public class ReconciliationOptions
    {

        /// <summary>
        /// The base interval between periodic reconciliation cycles for drift detection.
        /// CRD changes are still reconciled immediately via watch events.
        /// Default: 10 minutes - drift is rare and not urgent.
        /// </summary>
        public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Enable jitter to spread reconciliations across time.
        /// When enabled, each entity gets a deterministic offset based on its name,
        /// preventing all entities from reconciling simultaneously.
        /// </summary>
        public bool EnableJitter { get; set; } = true;

        /// <summary>
        /// Maximum jitter to add to reconciliation interval.
        /// Entities will be spread across [0, MaxJitter) window.
        /// Default: 5 minutes (~50% of interval for good spread).
        /// </summary>
        public TimeSpan MaxJitter { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Enable spreading initial reconciliations when operator starts.
        /// This prevents a thundering herd on operator startup.
        /// </summary>
        public bool EnableStartupSpread { get; set; } = true;

        /// <summary>
        /// Window over which to spread initial reconciliations on startup.
        /// Default: 2 minutes - provides headroom for large deployments (100+ resources).
        /// </summary>
        public TimeSpan StartupSpreadWindow { get; set; } = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Minimum interval between reconciliations when rate limited or approaching limits.
        /// Default: 5 minutes - backs off harder when under pressure.
        /// </summary>
        public TimeSpan MinThrottledInterval { get; set; } = TimeSpan.FromMinutes(5);

    }

}
