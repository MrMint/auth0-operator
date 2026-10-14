using System;
using System.Threading;
using System.Threading.Tasks;

using Alethic.Auth0.Operator.Controllers;
using Alethic.Auth0.Operator.Models;

using KubeOps.Abstractions.Finalizer;

namespace Alethic.Auth0.Operator.Finalizers
{

    /// <summary>
    /// Finalizer for V1RolePermission resources. Ensures permissions are removed from the role in Auth0 when the Kubernetes resource is deleted.
    /// </summary>
    public class V1RolePermissionFinalizer : IEntityFinalizer<V1RolePermission>
    {

        readonly V1RolePermissionController _controller;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="controller"></param>
        public V1RolePermissionFinalizer(V1RolePermissionController controller)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        }

        /// <inheritdoc />
        public async Task FinalizeAsync(V1RolePermission entity, CancellationToken cancellationToken)
        {
            await _controller.DeletedAsync(entity, cancellationToken);
        }

    }

}
