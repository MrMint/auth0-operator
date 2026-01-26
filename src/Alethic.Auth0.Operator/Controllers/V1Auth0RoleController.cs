using System;
using System.Collections;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Alethic.Auth0.Operator.Core.Models.Role;
using Alethic.Auth0.Operator.Models;
using Alethic.Auth0.Operator.Options;
using Alethic.Auth0.Operator.RateLimiting;

using Auth0.Core.Exceptions;
using Auth0.ManagementApi;
using Auth0.ManagementApi.Models;

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
    /// Controller for managing Auth0 Role resources.
    /// </summary>
    [EntityRbac(typeof(V1Tenant), Verbs = RbacVerb.List | RbacVerb.Get)]
    [EntityRbac(typeof(V1Auth0Role), Verbs = RbacVerb.All)]
    [EntityRbac(typeof(V1Secret), Verbs = RbacVerb.List | RbacVerb.Get)]
    [EntityRbac(typeof(Eventsv1Event), Verbs = RbacVerb.All)]
    public class V1Auth0RoleController :
        V1TenantEntityController<V1Auth0Role, V1Auth0Role.SpecDef, V1Auth0Role.StatusDef, RoleConf>,
        IEntityController<V1Auth0Role>
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
        public V1Auth0RoleController(
            IKubernetesClient kube,
            EntityRequeue<V1Auth0Role> requeue,
            IMemoryCache cache,
            ILogger<V1Auth0RoleController> logger,
            IOptions<OperatorOptions> options,
            IManagementApiClientFactory clientFactory,
            IRateLimiterService rateLimiterService,
            IReconciliationScheduler reconciliationScheduler
        )
            : base(kube, requeue, cache, logger, options, clientFactory, rateLimiterService, reconciliationScheduler)
        {

        }

        /// <inheritdoc />
        protected override string EntityTypeName => "Role";

        /// <inheritdoc />
        protected override async Task<Hashtable?> Get(IManagementApiClient api, string id, string defaultNamespace, CancellationToken cancellationToken)
        {
            try
            {
                return TransformToSystemTextJson<Hashtable>(await api.Roles.GetAsync(id, cancellationToken));
            }
            catch (ErrorApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        /// <inheritdoc />
        protected override async Task<string?> Find(IManagementApiClient api, V1Auth0Role entity, V1Auth0Role.SpecDef spec, string defaultNamespace, CancellationToken cancellationToken)
        {
            if (spec.Find is not null)
            {
                // If an ID is specified directly, try to find by ID
                if (spec.Find.Id is string roleId)
                {
                    try
                    {
                        var role = await api.Roles.GetAsync(roleId, cancellationToken);
                        Logger.LogInformation("{EntityTypeName} {EntityNamespace}/{EntityName} found existing role by ID: {Name}", EntityTypeName, entity.Namespace(), entity.Name(), role.Name);
                        return role.Id;
                    }
                    catch (ErrorApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
                    {
                        Logger.LogInformation("{EntityTypeName} {EntityNamespace}/{EntityName} could not find role with id {RoleId}.", EntityTypeName, entity.Namespace(), entity.Name(), roleId);
                        return null;
                    }
                }

                // If a name filter is specified, search by name
                if (spec.Find.NameFilter is string nameFilter)
                {
                    var roles = await api.Roles.GetAllAsync(new GetRolesRequest() { NameFilter = nameFilter }, cancellationToken: cancellationToken);
                    var role = roles.FirstOrDefault(r => r.Name == nameFilter);
                    if (role is not null)
                    {
                        Logger.LogInformation("{EntityTypeName} {EntityNamespace}/{EntityName} found existing role by name: {Name}", EntityTypeName, entity.Namespace(), entity.Name(), role.Name);
                        return role.Id;
                    }
                    Logger.LogInformation("{EntityTypeName} {EntityNamespace}/{EntityName} could not find role with name filter {NameFilter}.", EntityTypeName, entity.Namespace(), entity.Name(), nameFilter);
                    return null;
                }

                return null;
            }
            else
            {
                // Default: search by name from conf
                var conf = spec.Init ?? spec.Conf;
                if (conf is null)
                    return null;

                if (string.IsNullOrWhiteSpace(conf.Name))
                    return null;

                var roles = await api.Roles.GetAllAsync(new GetRolesRequest() { NameFilter = conf.Name }, cancellationToken: cancellationToken);
                var self = roles.FirstOrDefault(i => i.Name == conf.Name);
                return self?.Id;
            }
        }

        /// <inheritdoc />
        protected override string? ValidateCreate(RoleConf conf)
        {
            if (string.IsNullOrWhiteSpace(conf.Name))
                return "missing a value for name";

            return null;
        }

        /// <inheritdoc />
        protected override async Task<string> Create(IManagementApiClient api, RoleConf conf, string defaultNamespace, CancellationToken cancellationToken)
        {
            Logger.LogInformation("{EntityTypeName} creating role in Auth0 with name: {RoleName}", EntityTypeName, conf.Name);
            var request = TransformToNewtonsoftJson<RoleConf, RoleCreateRequest>(conf);
            var self = await api.Roles.CreateAsync(request, cancellationToken);
            Logger.LogInformation("{EntityTypeName} successfully created role in Auth0 with ID: {RoleId} and name: {RoleName}", EntityTypeName, self.Id, conf.Name);
            return self.Id;
        }

        /// <inheritdoc />
        protected override async Task Update(IManagementApiClient api, string id, Hashtable? last, RoleConf conf, string defaultNamespace, CancellationToken cancellationToken)
        {
            Logger.LogInformation("{EntityTypeName} updating role in Auth0 with id: {RoleId} and name: {RoleName}", EntityTypeName, id, conf.Name);
            var request = TransformToNewtonsoftJson<RoleConf, RoleUpdateRequest>(conf);
            await api.Roles.UpdateAsync(id, request, cancellationToken);
            Logger.LogInformation("{EntityTypeName} successfully updated role in Auth0 with id: {RoleId} and name: {RoleName}", EntityTypeName, id, conf.Name);
        }

        /// <inheritdoc />
        protected override Task Delete(IManagementApiClient api, string id, CancellationToken cancellationToken)
        {
            Logger.LogInformation("{EntityTypeName} deleting role from Auth0 with ID: {RoleId}", EntityTypeName, id);
            return api.Roles.DeleteAsync(id, cancellationToken);
        }

    }

}
