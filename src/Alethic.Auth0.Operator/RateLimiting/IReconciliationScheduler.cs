using System.Threading;
using System.Threading.Tasks;

namespace Alethic.Auth0.Operator.RateLimiting
{
    /// <summary>
    /// Centralized orchestration of all requeue/delay decisions for reconciliation.
    /// Handles startup spread, proactive throttling, and rate limit state.
    /// </summary>
    public interface IReconciliationScheduler
    {
        /// <summary>
        /// Evaluates whether reconciliation should proceed or be deferred.
        /// Checks startup spread, proactive throttling, and rate limit state.
        /// </summary>
        /// <param name="entityKey">The entity identifier (namespace/name).</param>
        /// <param name="tenantKey">The tenant identifier (namespace/name).</param>
        /// <param name="estimatedCalls">Estimated number of API calls for this reconciliation.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A scheduling decision indicating whether to proceed or defer.</returns>
        Task<SchedulingDecision> BeginReconcileAsync(
            string entityKey,
            string tenantKey,
            int estimatedCalls = 1,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Records completion of a reconciliation attempt.
        /// </summary>
        /// <param name="entityKey">The entity identifier.</param>
        /// <param name="success">Whether the reconciliation was successful.</param>
        void EndReconcile(string entityKey, bool success);

        /// <summary>
        /// Checks if an entity has been reconciled since operator startup.
        /// </summary>
        /// <param name="entityKey">The entity identifier.</param>
        /// <returns>True if the entity has been reconciled at least once.</returns>
        bool HasReconciledSinceStartup(string entityKey);
    }
}
