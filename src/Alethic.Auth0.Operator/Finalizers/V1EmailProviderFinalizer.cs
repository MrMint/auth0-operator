using System;
using System.Threading;
using System.Threading.Tasks;

using Alethic.Auth0.Operator.Controllers;
using Alethic.Auth0.Operator.Models;

using KubeOps.Abstractions.Finalizer;

namespace Alethic.Auth0.Operator.Finalizers
{

    /// <summary>
    /// Finalizer for V1EmailProvider resources. Removes the email provider from Auth0 on deletion only when the Delete policy is set.
    /// </summary>
    public class V1EmailProviderFinalizer : IEntityFinalizer<V1EmailProvider>
    {

        readonly V1EmailProviderController _controller;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="controller"></param>
        public V1EmailProviderFinalizer(V1EmailProviderController controller)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        }

        /// <inheritdoc />
        public async Task FinalizeAsync(V1EmailProvider entity, CancellationToken cancellationToken)
        {
            await _controller.DeletedAsync(entity, cancellationToken);
        }

    }

}
