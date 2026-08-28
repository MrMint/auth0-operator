using System;
using System.Threading;
using System.Threading.Tasks;

using Alethic.Auth0.Operator.Controllers;
using Alethic.Auth0.Operator.Models;

using KubeOps.Abstractions.Finalizer;

namespace Alethic.Auth0.Operator.Finalizers
{

    /// <summary>
    /// Finalizer for V1Auth0Role resources. Ensures cleanup in Auth0 when the Kubernetes resource is deleted.
    /// </summary>
    public class V1Auth0RoleFinalizer : IEntityFinalizer<V1Auth0Role>
    {

        readonly V1Auth0RoleController _controller;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="controller"></param>
        public V1Auth0RoleFinalizer(V1Auth0RoleController controller)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        }

        /// <inheritdoc />
        public async Task FinalizeAsync(V1Auth0Role entity, CancellationToken cancellationToken)
        {
            await _controller.DeletedAsync(entity, cancellationToken);
        }

    }

}
