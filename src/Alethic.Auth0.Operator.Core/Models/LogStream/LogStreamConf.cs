using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Alethic.Auth0.Operator.Core.Models.LogStream
{

    /// <summary>
    /// Filter configuration for log streams.
    /// </summary>
    public class LogStreamFilter
    {

        /// <summary>
        /// Filter by log event type.
        /// </summary>
        [JsonPropertyName("type")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Type { get; set; }

        /// <summary>
        /// Filter by log event name.
        /// </summary>
        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }

    }

    /// <summary>
    /// Find criteria for adopting existing Auth0 LogStreams.
    /// </summary>
    public class LogStreamFind
    {

        /// <summary>
        /// Find by LogStream ID.
        /// </summary>
        [JsonPropertyName("id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Id { get; set; }

        /// <summary>
        /// Find by LogStream name.
        /// </summary>
        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }

    }

    /// <summary>
    /// Configuration for an Auth0 LogStream.
    /// </summary>
    public class LogStreamConf
    {

        /// <summary>
        /// The name of the log stream.
        /// </summary>
        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }

        /// <summary>
        /// The type of log stream destination.
        /// </summary>
        [JsonPropertyName("type")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Type { get; set; }

        /// <summary>
        /// The status of the log stream: active, paused, suspended.
        /// </summary>
        [JsonPropertyName("status")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Status { get; set; }

        /// <summary>
        /// Filters to apply to the log stream.
        /// </summary>
        [JsonPropertyName("filters")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<LogStreamFilter>? Filters { get; set; }

        /// <summary>
        /// HTTP webhook sink configuration.
        /// </summary>
        [JsonPropertyName("sink")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public LogStreamSink? Sink { get; set; }

    }

    /// <summary>
    /// Discriminated union for log stream sink configurations.
    /// Only one sink type should be populated based on LogStreamType.
    /// </summary>
    public class LogStreamSink
    {

        /// <summary>
        /// HTTP Webhook sink configuration.
        /// </summary>
        [JsonPropertyName("http")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public HttpSinkConfig? Http { get; set; }

        /// <summary>
        /// AWS EventBridge sink configuration.
        /// </summary>
        [JsonPropertyName("eventBridge")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public EventBridgeSinkConfig? EventBridge { get; set; }

        /// <summary>
        /// Azure Event Grid sink configuration.
        /// </summary>
        [JsonPropertyName("eventGrid")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public EventGridSinkConfig? EventGrid { get; set; }

        /// <summary>
        /// Datadog sink configuration.
        /// </summary>
        [JsonPropertyName("datadog")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DatadogSinkConfig? Datadog { get; set; }

        /// <summary>
        /// Splunk sink configuration.
        /// </summary>
        [JsonPropertyName("splunk")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SplunkSinkConfig? Splunk { get; set; }

        /// <summary>
        /// Sumo Logic sink configuration.
        /// </summary>
        [JsonPropertyName("sumo")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SumoSinkConfig? Sumo { get; set; }

        /// <summary>
        /// Mixpanel sink configuration.
        /// </summary>
        [JsonPropertyName("mixpanel")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public MixpanelSinkConfig? Mixpanel { get; set; }

        /// <summary>
        /// Segment sink configuration.
        /// </summary>
        [JsonPropertyName("segment")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SegmentSinkConfig? Segment { get; set; }

    }

}
