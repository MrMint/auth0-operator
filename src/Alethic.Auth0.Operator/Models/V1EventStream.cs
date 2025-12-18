using System.Collections;
using System.Collections.Generic;
using System.Text.Json.Serialization;

using Alethic.Auth0.Operator.Core.Extensions;
using Alethic.Auth0.Operator.Core.Models;
using Alethic.Auth0.Operator.Core.Models.EventStream;

using k8s.Models;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

namespace Alethic.Auth0.Operator.Models
{

    /// <summary>
    /// Represents an Auth0 EventStream resource in Kubernetes (Beta).
    /// EventStreams provide CloudEvents-compliant user lifecycle events.
    /// Currently supports EventBridge and Webhook destinations.
    /// </summary>
    [EntityScope(EntityScope.Namespaced)]
    [KubernetesEntity(Group = "kubernetes.auth0.com", ApiVersion = "v1", Kind = "EventStream", PluralName = "eventstreams")]
    [KubernetesEntityShortNames("a0es")]
    public partial class V1EventStream :
        CustomKubernetesEntity<V1EventStream.SpecDef, V1EventStream.StatusDef>,
        V1TenantEntity<V1EventStream.SpecDef, V1EventStream.StatusDef, EventStreamConf>
    {

        public class SpecDef : V1TenantEntitySpec<EventStreamConf>
        {

            /// <summary>
            /// Set of operations allowed with the entity.
            /// Valid values: Create, Update, Delete
            /// </summary>
            [JsonPropertyName("policy")]
            public V1EntityPolicyType[]? Policy { get; set; }

            /// <summary>
            /// Reference to the Tenant resource that owns this event stream.
            /// </summary>
            [JsonPropertyName("tenantRef")]
            [Required]
            public V1TenantReference? TenantRef { get; set; }

            /// <summary>
            /// Criteria for finding existing Auth0 event streams to adopt.
            /// </summary>
            [JsonPropertyName("find")]
            public EventStreamFind? Find { get; set; }

            /// <summary>
            /// Initial configuration used only during event stream creation.
            /// Falls back to Conf if not specified.
            /// </summary>
            [JsonPropertyName("init")]
            public EventStreamConf? Init { get; set; }

            /// <summary>
            /// EventStream configuration matching Auth0 Management API schema.
            /// </summary>
            [JsonPropertyName("conf")]
            [Required]
            public EventStreamConf? Conf { get; set; }

        }

        public class StatusDef : V1TenantEntityStatus
        {

            /// <summary>
            /// Auth0 EventStream ID (format: est_XXXXX)
            /// </summary>
            [JsonPropertyName("id")]
            public string? Id { get; set; }

            /// <summary>
            /// Current status of the event stream: active, paused.
            /// </summary>
            [JsonPropertyName("currentStatus")]
            public string? CurrentStatus { get; set; }

            /// <summary>
            /// The type of event stream destination.
            /// </summary>
            [JsonPropertyName("type")]
            public string? Type { get; set; }

            /// <summary>
            /// List of subscribed event types.
            /// </summary>
            [JsonPropertyName("subscribedEvents")]
            public List<string>? SubscribedEvents { get; set; }

            /// <summary>
            /// Last synced configuration state.
            /// </summary>
            [JsonPropertyName("lastConf")]
            [JsonConverter(typeof(SimplePrimitiveHashtableConverter))]
            public Hashtable? LastConf { get; set; }

        }

    }

}
