using System.Collections;
using System.Text.Json.Serialization;

using Alethic.Auth0.Operator.Core.Extensions;
using Alethic.Auth0.Operator.Core.Models;
using Alethic.Auth0.Operator.Core.Models.Role;

using k8s.Models;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

namespace Alethic.Auth0.Operator.Models
{

    /// <summary>
    /// Represents an Auth0 Role resource in Kubernetes.
    /// Roles are used to organize and grant permissions to users.
    /// Named V1Auth0Role to avoid conflict with k8s.Models.V1Role.
    /// </summary>
    [EntityScope(EntityScope.Namespaced)]
    [KubernetesEntity(Group = "kubernetes.auth0.com", ApiVersion = "v1", Kind = "Auth0Role", PluralName = "auth0roles")]
    [KubernetesEntityShortNames("a0role")]
    public partial class V1Auth0Role :
        CustomKubernetesEntity<V1Auth0Role.SpecDef, V1Auth0Role.StatusDef>,
        V1TenantEntity<V1Auth0Role.SpecDef, V1Auth0Role.StatusDef, RoleConf>
    {

        public class SpecDef : V1TenantEntitySpec<RoleConf>
        {

            /// <summary>
            /// Set of operations allowed with the entity.
            /// Valid values: Create, Update, Delete
            /// </summary>
            [JsonPropertyName("policy")]
            public V1EntityPolicyType[]? Policy { get; set; }

            /// <summary>
            /// Reference to the Tenant resource that owns this role.
            /// </summary>
            [JsonPropertyName("tenantRef")]
            [Required]
            public V1TenantReference? TenantRef { get; set; }

            /// <summary>
            /// Criteria for finding existing Auth0 roles to adopt.
            /// </summary>
            [JsonPropertyName("find")]
            public RoleFind? Find { get; set; }

            /// <summary>
            /// Initial configuration used only during role creation.
            /// Falls back to Conf if not specified.
            /// </summary>
            [JsonPropertyName("init")]
            public RoleConf? Init { get; set; }

            /// <summary>
            /// Role configuration matching Auth0 Management API schema.
            /// </summary>
            [JsonPropertyName("conf")]
            [Required]
            public RoleConf? Conf { get; set; }

        }

        public class StatusDef : V1TenantEntityStatus
        {

            /// <summary>
            /// Auth0 Role ID (format: rol_XXXXX)
            /// </summary>
            [JsonPropertyName("id")]
            public string? Id { get; set; }

            /// <summary>
            /// Hash of last synced configuration for drift detection.
            /// </summary>
            [JsonPropertyName("lastConf")]
            [JsonConverter(typeof(SimplePrimitiveHashtableConverter))]
            public Hashtable? LastConf { get; set; }

        }

    }

}
