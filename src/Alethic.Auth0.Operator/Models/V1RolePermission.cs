using System.Collections.Generic;
using System.Text.Json.Serialization;

using Alethic.Auth0.Operator.Core.Extensions;
using Alethic.Auth0.Operator.Core.Models;
using Alethic.Auth0.Operator.Core.Models.RolePermission;

using k8s.Models;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

namespace Alethic.Auth0.Operator.Models
{

    /// <summary>
    /// Represents Auth0 Role Permission associations in Kubernetes.
    /// Links permissions from a ResourceServer to a Role.
    /// </summary>
    [EntityScope(EntityScope.Namespaced)]
    [KubernetesEntity(Group = "kubernetes.auth0.com", ApiVersion = "v1", Kind = "RolePermission", PluralName = "rolepermissions")]
    [KubernetesEntityShortNames("a0rp")]
    public partial class V1RolePermission :
        CustomKubernetesEntity<V1RolePermission.SpecDef, V1RolePermission.StatusDef>,
        V1TenantEntity<V1RolePermission.SpecDef, V1RolePermission.StatusDef, RolePermissionConf>
    {

        public class SpecDef : V1TenantEntitySpec<RolePermissionConf>
        {

            /// <summary>
            /// Set of operations allowed with the entity.
            /// Valid values: Create, Delete (Update is handled via diff)
            /// </summary>
            [JsonPropertyName("policy")]
            public V1EntityPolicyType[]? Policy { get; set; }

            /// <summary>
            /// Reference to the Tenant resource that owns this role permission.
            /// </summary>
            [JsonPropertyName("tenantRef")]
            [Required]
            public V1TenantReference? TenantRef { get; set; }

            /// <summary>
            /// Reference to the Role resource to assign permissions to.
            /// </summary>
            [JsonPropertyName("roleRef")]
            [Required]
            public V1RoleReference? RoleRef { get; set; }

            /// <summary>
            /// Reference to the ResourceServer that defines the permissions.
            /// </summary>
            [JsonPropertyName("resourceServerRef")]
            [Required]
            public V1ResourceServerReference? ResourceServerRef { get; set; }

            /// <summary>
            /// Initial configuration used only during creation.
            /// Falls back to Conf if not specified.
            /// </summary>
            [JsonPropertyName("init")]
            public RolePermissionConf? Init { get; set; }

            /// <summary>
            /// Role permission configuration containing the list of permissions.
            /// </summary>
            [JsonPropertyName("conf")]
            [Required]
            public RolePermissionConf? Conf { get; set; }

        }

        public class StatusDef : V1TenantEntityStatus
        {

            /// <summary>
            /// Composite ID representing the role-resourceserver association.
            /// Format: {roleId}:{resourceServerIdentifier}
            /// </summary>
            [JsonPropertyName("id")]
            public string? Id { get; set; }

            /// <summary>
            /// The Auth0 Role ID this permission set is associated with.
            /// </summary>
            [JsonPropertyName("roleAuth0Id")]
            public string? RoleAuth0Id { get; set; }

            /// <summary>
            /// The Resource Server identifier (audience) this permission set comes from.
            /// </summary>
            [JsonPropertyName("resourceServerIdentifier")]
            public string? ResourceServerIdentifier { get; set; }

            /// <summary>
            /// List of permissions currently synced to Auth0.
            /// </summary>
            [JsonPropertyName("syncedPermissions")]
            public List<string>? SyncedPermissions { get; set; }

            /// <summary>
            /// Last synced configuration state.
            /// </summary>
            [JsonPropertyName("lastConf")]
            [JsonConverter(typeof(SimplePrimitiveHashtableConverter))]
            public System.Collections.Hashtable? LastConf { get; set; }

        }

    }

}
