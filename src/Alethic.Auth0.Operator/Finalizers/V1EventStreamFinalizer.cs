using System;
using System.Threading;
using System.Threading.Tasks;

using Alethic.Auth0.Operator.Controllers;
using Alethic.Auth0.Operator.Models;

using KubeOps.Abstractions.Finalizer;

namespace Alethic.Auth0.Operator.Finalizers
{

    /// <summary>
    /// Finalizer for V1EventStream resources. Ensures cleanup in Auth0 when the Kubernetes resource is deleted.
    /// Note: EventStreams API is in Early Access (Beta).
    /// </summary>
    public class V1EventStreamFinalizer : IEntityFinalizer<V1EventStream>
    {

        readonly V1EventStreamController _controller;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="controller"></param>
        public V1EventStreamFinalizer(V1EventStreamController controller)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        }

        /// <inheritdoc />
        public async Task FinalizeAsync(V1EventStream entity, CancellationToken cancellationToken)
        {
            await _controller.DeletedAsync(entity, cancellationToken);
        }

    }

}
