using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Alethic.Auth0.Operator.Core.Models;
using Alethic.Auth0.Operator.Core.Models.RolePermission;
using Alethic.Auth0.Operator.Models;
using Alethic.Auth0.Operator.Options;
using Alethic.Auth0.Operator.RateLimiting;
using Auth0.Core.Exceptions;
using Auth0.ManagementApi;
using Auth0.ManagementApi.Models;
using Auth0.ManagementApi.Paging;
using k8s.Models;
using KubeOps.Abstractions.Controller;
using KubeOps.Abstractions.Queue;
using KubeOps.Abstractions.Rbac;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alethic.Auth0.Operator.Controllers
{
    /// <summary>
    /// Controller for managing Auth0 Role Permission associations.
    /// Handles the assignment and removal of permissions from ResourceServers to Roles.
    /// </summary>
    [EntityRbac(typeof(V1Tenant), Verbs = RbacVerb.List | RbacVerb.Get)]
    [EntityRbac(typeof(V1Auth0Role), Verbs = RbacVerb.List | RbacVerb.Get)]
    [EntityRbac(typeof(V1ResourceServer), Verbs = RbacVerb.List | RbacVerb.Get)]
    [EntityRbac(typeof(V1RolePermission), Verbs = RbacVerb.All)]
    [EntityRbac(typeof(V1Secret), Verbs = RbacVerb.List | RbacVerb.Get)]
    [EntityRbac(typeof(Eventsv1Event), Verbs = RbacVerb.All)]
    public class V1RolePermissionController
        : V1TenantEntityController<
            V1RolePermission,
            V1RolePermission.SpecDef,
            V1RolePermission.StatusDef,
            RolePermissionConf
        >,
            IEntityController<V1RolePermission>
    {
        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="kube"></param>
        /// <param name="requeue"></param>
        /// <param name="cache"></param>
        /// <param name="logger"></param>
        /// <param name="options"></param>
        /// <param name="clientFactory"></param>
        /// <param name="rateLimiterService"></param>
        /// <param name="reconciliationScheduler"></param>
        public V1RolePermissionController(
            IKubernetesClient kube,
            EntityRequeue<V1RolePermission> requeue,
            IMemoryCache cache,
            ILogger<V1RolePermissionController> logger,
            IOptions<OperatorOptions> options,
            IManagementApiClientFactory clientFactory,
            IRateLimiterService rateLimiterService,
            IReconciliationScheduler reconciliationScheduler
        )
            : base(kube, requeue, cache, logger, options, clientFactory, rateLimiterService, reconciliationScheduler) { }

        /// <inheritdoc />
        protected override string EntityTypeName => "RolePermission";

        /// <inheritdoc />
        protected override async Task<Hashtable?> Get(
            IManagementApiClient api,
            string id,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            // ID format is {roleId}:{resourceServerIdentifier}
            var parts = id.Split(':', 2);
            if (parts.Length != 2)
                return null;

            var roleId = parts[0];
            var resourceServerIdentifier = parts[1];

            try
            {
                // Verify the role still exists
                var role = await api.Roles.GetAsync(roleId, cancellationToken);
                if (role == null)
                    return null;

                // Get current permissions for this role and filter by resource server
                var permissions = await GetAllRolePermissionsAsync(api, roleId, cancellationToken);
                var filtered = permissions
                    .Where(p => p.Identifier == resourceServerIdentifier)
                    .ToList();

                // Return a hashtable representing the current state
                var result = new Hashtable
                {
                    ["role_id"] = roleId,
                    ["resource_server_identifier"] = resourceServerIdentifier,
                    ["permissions"] = filtered.Select(p => p.Name).ToList(),
                };

                return result;
            }
            catch (ErrorApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        /// <inheritdoc />
        protected override async Task<string?> Find(
            IManagementApiClient api,
            V1RolePermission entity,
            V1RolePermission.SpecDef spec,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            if (spec.RoleRef is null)
                throw new InvalidOperationException("RoleRef is required.");

            if (spec.ResourceServerRef is null)
                throw new InvalidOperationException("ResourceServerRef is required.");

            // Resolve role reference to get the Auth0 Role ID
            // Note: ResolveRoleRefToIdAsync throws RetryException if the referenced resource hasn't been reconciled yet
            var roleId = await ResolveRoleRefToIdAsync(
                api,
                spec.RoleRef,
                defaultNamespace,
                cancellationToken
            );
            if (string.IsNullOrWhiteSpace(roleId))
                throw new InvalidOperationException(
                    "Failed to resolve RoleRef to an Auth0 Role ID."
                );

            // Resolve resource server reference to get the identifier
            // Note: ResolveResourceServerRefToIdentifier throws RetryException if the referenced resource hasn't been reconciled yet
            var resourceServerIdentifier = await ResolveResourceServerRefToIdentifier(
                api,
                spec.ResourceServerRef,
                defaultNamespace,
                cancellationToken
            );
            if (string.IsNullOrWhiteSpace(resourceServerIdentifier))
                throw new InvalidOperationException(
                    "Failed to resolve ResourceServerRef to an identifier."
                );

            // The "ID" for a RolePermission is a composite of role ID and resource server identifier.
            // This always returns a value once references are resolved - the permissions may or may not exist yet.
            return $"{roleId}:{resourceServerIdentifier}";
        }

        /// <inheritdoc />
        protected override string? ValidateCreate(RolePermissionConf conf)
        {
            if (conf.Permissions == null || conf.Permissions.Count == 0)
                return "missing permissions list";

            return null;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Note: For RolePermission, Create() is not called in the normal flow because Find() always returns
        /// a composite ID once references are resolved. This method exists only to satisfy the base class contract.
        /// The actual permission assignment happens in Update() which handles the diff between desired and current state.
        ///
        /// If this is ever called, it indicates a logic error in the base class flow or a bug in Find().
        /// </remarks>
        protected override Task<string> Create(
            IManagementApiClient api,
            RolePermissionConf conf,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            // This should never be called because Find() always returns a composite ID once references resolve.
            // If we get here, it means Find() returned null which should only happen if references are invalid,
            // but that case now throws InvalidOperationException instead.
            throw new InvalidOperationException(
                "RolePermission.Create() should never be called. "
                    + "Find() should always return a composite ID once role and resource server references are resolved. "
                    + "If you see this error, there is a bug in the Find() implementation."
            );
        }

        /// <inheritdoc />
        protected override async Task Update(
            IManagementApiClient api,
            string id,
            Hashtable? last,
            RolePermissionConf conf,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            var parts = id.Split(':', 2);
            if (parts.Length != 2)
                throw new InvalidOperationException($"Invalid RolePermission ID format: {id}");

            var roleId = parts[0];
            var resourceServerIdentifier = parts[1];

            var desiredPermissions = conf.Permissions ?? new List<string>();

            // Get current permissions for this role from this specific resource server
            var currentPermissions = await GetAllRolePermissionsAsync(
                api,
                roleId,
                cancellationToken
            );
            var currentForResourceServer = currentPermissions
                .Where(p => p.Identifier == resourceServerIdentifier)
                .Select(p => p.Name)
                .ToHashSet();

            // Calculate diff
            var permissionsToAdd = desiredPermissions
                .Where(p => !currentForResourceServer.Contains(p))
                .ToList();
            var permissionsToRemove = currentForResourceServer
                .Where(p => !desiredPermissions.Contains(p))
                .ToList();

            // Add new permissions
            if (permissionsToAdd.Count > 0)
            {
                Logger.LogInformation(
                    "{EntityTypeName} adding {Count} permissions to role {RoleId} from {ResourceServer}: {Permissions}",
                    EntityTypeName,
                    permissionsToAdd.Count,
                    roleId,
                    resourceServerIdentifier,
                    string.Join(", ", permissionsToAdd)
                );

                var permissionsToAssign = permissionsToAdd
                    .Select(p => new PermissionIdentity
                    {
                        Identifier = resourceServerIdentifier,
                        Name = p,
                    })
                    .ToList();

                await api.Roles.AssignPermissionsAsync(
                    roleId,
                    new AssignPermissionsRequest { Permissions = permissionsToAssign },
                    cancellationToken
                );
            }

            // Remove old permissions
            if (permissionsToRemove.Count > 0)
            {
                Logger.LogInformation(
                    "{EntityTypeName} removing {Count} permissions from role {RoleId} from {ResourceServer}: {Permissions}",
                    EntityTypeName,
                    permissionsToRemove.Count,
                    roleId,
                    resourceServerIdentifier,
                    string.Join(", ", permissionsToRemove)
                );

                var permissionsToUnassign = permissionsToRemove
                    .Select(p => new PermissionIdentity
                    {
                        Identifier = resourceServerIdentifier,
                        Name = p,
                    })
                    .ToList();

                await api.Roles.RemovePermissionsAsync(
                    roleId,
                    new AssignPermissionsRequest { Permissions = permissionsToUnassign },
                    cancellationToken
                );
            }

            if (permissionsToAdd.Count == 0 && permissionsToRemove.Count == 0)
            {
                Logger.LogDebug(
                    "{EntityTypeName} permissions for role {RoleId} from {ResourceServer} are already in sync",
                    EntityTypeName,
                    roleId,
                    resourceServerIdentifier
                );
            }
        }

        /// <inheritdoc />
        protected override async Task ApplyStatus(
            IManagementApiClient api,
            V1RolePermission entity,
            Hashtable lastConf,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            entity.Status.RoleAuth0Id = (string?)lastConf["role_id"];
            entity.Status.ResourceServerIdentifier = (string?)
                lastConf["resource_server_identifier"];
            entity.Status.SyncedPermissions = ((List<string>?)lastConf["permissions"])?.ToList();
            await base.ApplyStatus(api, entity, lastConf, defaultNamespace, cancellationToken);
        }

        /// <inheritdoc />
        protected override async Task Delete(
            IManagementApiClient api,
            string id,
            CancellationToken cancellationToken
        )
        {
            var parts = id.Split(':', 2);
            if (parts.Length != 2)
            {
                Logger.LogWarning(
                    "{EntityTypeName} invalid ID format for deletion: {Id}",
                    EntityTypeName,
                    id
                );
                return;
            }

            var roleId = parts[0];
            var resourceServerIdentifier = parts[1];

            // Get all permissions for this role from this resource server and remove them
            var currentPermissions = await GetAllRolePermissionsAsync(
                api,
                roleId,
                cancellationToken
            );
            var permissionsToRemove = currentPermissions
                .Where(p => p.Identifier == resourceServerIdentifier)
                .Select(p => new PermissionIdentity
                {
                    Identifier = resourceServerIdentifier,
                    Name = p.Name,
                })
                .ToList();

            if (permissionsToRemove.Count > 0)
            {
                Logger.LogInformation(
                    "{EntityTypeName} removing all {Count} permissions from role {RoleId} for resource server {ResourceServer}",
                    EntityTypeName,
                    permissionsToRemove.Count,
                    roleId,
                    resourceServerIdentifier
                );

                await api.Roles.RemovePermissionsAsync(
                    roleId,
                    new AssignPermissionsRequest { Permissions = permissionsToRemove },
                    cancellationToken
                );
            }
            else
            {
                Logger.LogDebug(
                    "{EntityTypeName} no permissions to remove from role {RoleId} for resource server {ResourceServer}",
                    EntityTypeName,
                    roleId,
                    resourceServerIdentifier
                );
            }
        }

        /// <summary>
        /// Gets all permissions for a role, handling pagination.
        /// Auth0 API has a 100 item per page maximum.
        /// </summary>
        private async Task<List<Permission>> GetAllRolePermissionsAsync(
            IManagementApiClient api,
            string roleId,
            CancellationToken cancellationToken
        )
        {
            var allPermissions = new List<Permission>();
            var page = 0;
            const int perPage = 100;

            while (true)
            {
                var response = await api.Roles.GetPermissionsAsync(
                    roleId,
                    new PaginationInfo(page, perPage, true),
                    cancellationToken
                );

                allPermissions.AddRange(response);

                if (response.Count < perPage)
                    break;

                page++;

                // Safety limit to prevent infinite loops
                if (page > 100)
                {
                    Logger.LogWarning(
                        "{EntityTypeName} hit pagination safety limit for role {RoleId}",
                        EntityTypeName,
                        roleId
                    );
                    break;
                }
            }

            return allPermissions;
        }

        /// <summary>
        /// Resolves a V1RoleReference to an Auth0 Role ID.
        /// </summary>
        private async Task<string?> ResolveRoleRefToIdAsync(
            IManagementApiClient api,
            V1RoleReference? roleRef,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            if (roleRef is null)
                return null;

            // If ID is specified directly, use it
            if (!string.IsNullOrWhiteSpace(roleRef.Id))
                return roleRef.Id;

            // Otherwise, look up the V1Auth0Role resource
            if (string.IsNullOrWhiteSpace(roleRef.Name))
                throw new InvalidOperationException("Role reference has no name.");

            var ns = roleRef.Namespace ?? defaultNamespace;
            if (string.IsNullOrWhiteSpace(ns))
                throw new InvalidOperationException("Role reference has no namespace.");

            var role = await Kube.GetAsync<V1Auth0Role>(roleRef.Name, ns, cancellationToken);
            if (role is null)
                throw new RetryException($"Role reference {ns}/{roleRef.Name} cannot be resolved.");

            if (string.IsNullOrWhiteSpace(role.Status?.Id))
                throw new RetryException(
                    $"Referenced Role {ns}/{roleRef.Name} has not been reconciled."
                );

            Logger.LogDebug(
                "Resolved RoleRef {Namespace}/{Name} to {Id}",
                ns,
                roleRef.Name,
                role.Status.Id
            );
            return role.Status.Id;
        }
    }
}
