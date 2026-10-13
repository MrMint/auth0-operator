using System.Collections;
using System.Text.Json.Serialization;

using Alethic.Auth0.Operator.Core.Extensions;
using Alethic.Auth0.Operator.Core.Models;
using Alethic.Auth0.Operator.Core.Models.EmailProvider;

using k8s.Models;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

namespace Alethic.Auth0.Operator.Models
{

    /// <summary>
    /// Represents the email provider of an Auth0 tenant. A tenant has at most one email provider,
    /// so only one EmailProvider resource may target a given tenant.
    /// </summary>
    [EntityScope(EntityScope.Namespaced)]
    [KubernetesEntity(Group = "kubernetes.auth0.com", ApiVersion = "v1", Kind = "EmailProvider", PluralName = "emailproviders")]
    [KubernetesEntityShortNames("a0email")]
    public partial class V1EmailProvider :
        CustomKubernetesEntity<V1EmailProvider.SpecDef, V1EmailProvider.StatusDef>,
        V1TenantEntity<V1EmailProvider.SpecDef, V1EmailProvider.StatusDef, EmailProviderConf>
    {

        public class SpecDef : V1TenantEntitySpec<EmailProviderConf>
        {

            /// <summary>
            /// Set of operations allowed with the entity. Defaults to Create and Update, so deleting
            /// the resource leaves the email provider configured in Auth0.
            /// Valid values: Create, Update, Delete
            /// </summary>
            [JsonPropertyName("policy")]
            public V1EntityPolicyType[]? Policy { get; set; }

            /// <summary>
            /// Reference to the Tenant resource whose email provider is managed.
            /// </summary>
            [JsonPropertyName("tenantRef")]
            [Required]
            public V1TenantReference? TenantRef { get; set; }

            /// <summary>
            /// Initial configuration used only when the email provider is first configured.
            /// Falls back to Conf if not specified.
            /// </summary>
            [JsonPropertyName("init")]
            public EmailProviderConf? Init { get; set; }

            /// <summary>
            /// Email provider configuration matching the Auth0 Management API schema.
            /// </summary>
            [JsonPropertyName("conf")]
            [Required]
            public EmailProviderConf? Conf { get; set; }

        }

        public class StatusDef : V1TenantEntityStatus
        {

            /// <summary>
            /// Fixed identifier set once the email provider exists. Auth0 does not assign one.
            /// </summary>
            [JsonPropertyName("id")]
            public string? Id { get; set; }

            /// <summary>
            /// Last observed non-secret configuration. Never contains credentials.
            /// </summary>
            [JsonPropertyName("lastConf")]
            [JsonConverter(typeof(SimplePrimitiveHashtableConverter))]
            public Hashtable? LastConf { get; set; }

        }

    }

}
