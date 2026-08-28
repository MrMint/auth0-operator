using System.Collections.Generic;
using System.Text.Json.Serialization;
using Alethic.Auth0.Operator.Core.Models;

namespace Alethic.Auth0.Operator.Core.Models.EventStream
{
    /// <summary>
    /// EventStream destination types.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum EventStreamType
    {
        [JsonStringEnumMemberName("eventbridge")]
        EventBridge,

        [JsonStringEnumMemberName("webhook")]
        Webhook,

        [JsonStringEnumMemberName("action")]
        Action
    }

    /// <summary>
    /// EventStream status values.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum EventStreamStatus
    {
        [JsonStringEnumMemberName("enabled")]
        Enabled,

        [JsonStringEnumMemberName("disabled")]
        Disabled
    }

    /// <summary>
    /// Supported event types for EventStreams.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum EventType
    {
        [JsonStringEnumMemberName("user.created")]
        UserCreated,

        [JsonStringEnumMemberName("user.updated")]
        UserUpdated,

        [JsonStringEnumMemberName("user.deleted")]
        UserDeleted,

        [JsonStringEnumMemberName("organization.created")]
        OrganizationCreated,

        [JsonStringEnumMemberName("organization.updated")]
        OrganizationUpdated,

        [JsonStringEnumMemberName("organization.deleted")]
        OrganizationDeleted,

        [JsonStringEnumMemberName("organization.member.added")]
        OrganizationMemberAdded,

        [JsonStringEnumMemberName("organization.member.deleted")]
        OrganizationMemberDeleted,

        [JsonStringEnumMemberName("organization.member.role.assigned")]
        OrganizationMemberRoleAssigned,

        [JsonStringEnumMemberName("organization.member.role.deleted")]
        OrganizationMemberRoleDeleted,

        [JsonStringEnumMemberName("organization.connection.added")]
        OrganizationConnectionAdded,

        [JsonStringEnumMemberName("organization.connection.updated")]
        OrganizationConnectionUpdated,

        [JsonStringEnumMemberName("organization.connection.removed")]
        OrganizationConnectionRemoved
    }

    /// <summary>
    /// Find criteria for adopting existing Auth0 EventStreams.
    /// </summary>
    public class EventStreamFind
    {
        /// <summary>
        /// Find by EventStream ID.
        /// </summary>
        [JsonPropertyName("id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Id { get; set; }

        /// <summary>
        /// Find by EventStream name.
        /// </summary>
        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }
    }

    /// <summary>
    /// Configuration for an Auth0 EventStream (Beta).
    /// EventStreams provide CloudEvents-compliant user lifecycle events.
    /// </summary>
    public class EventStreamConf
    {
        /// <summary>
        /// The name of the event stream.
        /// </summary>
        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }

        /// <summary>
        /// The type of event stream destination: eventbridge, webhook, or action.
        /// </summary>
        [JsonPropertyName("type")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public EventStreamType? Type { get; set; }

        /// <summary>
        /// The status of the event stream: enabled or disabled.
        /// </summary>
        [JsonPropertyName("status")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public EventStreamStatus? Status { get; set; }

        /// <summary>
        /// List of event types to subscribe to.
        /// </summary>
        [JsonPropertyName("subscriptions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<EventStreamSubscription>? Subscriptions { get; set; }

        /// <summary>
        /// Sink configuration for the event stream.
        /// </summary>
        [JsonPropertyName("sink")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public EventStreamSink? Sink { get; set; }
    }

    /// <summary>
    /// Event subscription configuration.
    /// </summary>
    public class EventStreamSubscription
    {
        /// <summary>
        /// The event type to subscribe to.
        /// </summary>
        [JsonPropertyName("eventType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public EventType? EventType { get; set; }
    }

    /// <summary>
    /// Sink configuration for EventStreams.
    /// </summary>
    public class EventStreamSink
    {
        /// <summary>
        /// AWS EventBridge sink configuration.
        /// </summary>
        [JsonPropertyName("eventBridge")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public EventStreamEventBridgeSink? EventBridge { get; set; }

        /// <summary>
        /// Webhook sink configuration.
        /// </summary>
        [JsonPropertyName("webhook")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public EventStreamWebhookSink? Webhook { get; set; }
    }

    /// <summary>
    /// EventBridge sink for EventStreams.
    /// </summary>
    public class EventStreamEventBridgeSink
    {
        /// <summary>
        /// AWS Account ID.
        /// </summary>
        [JsonPropertyName("awsAccountId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AwsAccountId { get; set; }

        /// <summary>
        /// AWS Region.
        /// </summary>
        [JsonPropertyName("awsRegion")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AwsRegion { get; set; }
    }

    /// <summary>
    /// Webhook sink for EventStreams.
    /// </summary>
    public class EventStreamWebhookSink
    {
        /// <summary>
        /// The webhook URL to send events to.
        /// </summary>
        [JsonPropertyName("url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Url { get; set; }

        /// <summary>
        /// Reference to secret containing authorization header value.
        /// </summary>
        [JsonPropertyName("authorizationSecretRef")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SecretKeySelector? AuthorizationSecretRef { get; set; }
    }
}
