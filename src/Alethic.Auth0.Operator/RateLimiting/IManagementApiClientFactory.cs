using System;

using Auth0.ManagementApi;

namespace Alethic.Auth0.Operator.RateLimiting
{
    /// <summary>
    /// Factory for creating <see cref="IManagementApiClient"/> instances with rate limit tracking.
    /// </summary>
    public interface IManagementApiClientFactory
    {
        /// <summary>
        /// Creates a new <see cref="IManagementApiClient"/> with rate limit tracking enabled.
        /// </summary>
        /// <param name="token">The Auth0 Management API access token.</param>
        /// <param name="baseUri">The base URI for the Auth0 Management API.</param>
        /// <param name="tenantKey">The tenant identifier for rate limit tracking.</param>
        /// <returns>A new <see cref="IManagementApiClient"/> instance.</returns>
        IManagementApiClient Create(string token, Uri baseUri, string tenantKey);
    }
}
