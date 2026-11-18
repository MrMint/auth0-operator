using System;
using System.Threading;
using System.Threading.Tasks;

using Alethic.Auth0.Operator.Controllers;
using Alethic.Auth0.Operator.Models;

using KubeOps.Abstractions.Finalizer;

namespace Alethic.Auth0.Operator.Finalizers
{

    /// <summary>
    /// Finalizer for V1LogStream resources. Ensures cleanup in Auth0 when the Kubernetes resource is deleted.
    /// </summary>
    public class V1LogStreamFinalizer : IEntityFinalizer<V1LogStream>
    {

        readonly V1LogStreamController _controller;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="controller"></param>
        public V1LogStreamFinalizer(V1LogStreamController controller)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        }

        /// <inheritdoc />
        public async Task FinalizeAsync(V1LogStream entity, CancellationToken cancellationToken)
        {
            await _controller.DeletedAsync(entity, cancellationToken);
        }

    }

}
