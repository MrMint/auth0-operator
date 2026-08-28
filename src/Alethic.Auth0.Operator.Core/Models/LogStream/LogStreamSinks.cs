using System.Collections.Generic;
using System.Text.Json.Serialization;
using Alethic.Auth0.Operator.Core.Models;

namespace Alethic.Auth0.Operator.Core.Models.LogStream
{
    /// <summary>
    /// HTTP header for webhook configuration.
    /// </summary>
    public class HttpHeader
    {
        /// <summary>
        /// Header name.
        /// </summary>
        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }

        /// <summary>
        /// Header value.
        /// </summary>
        [JsonPropertyName("value")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Value { get; set; }
    }

    /// <summary>
    /// Configuration for HTTP Webhook sink.
    /// </summary>
    public class HttpSinkConfig
    {
        /// <summary>
        /// The HTTP endpoint URL to send logs to.
        /// </summary>
        [JsonPropertyName("httpEndpoint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? HttpEndpoint { get; set; }

        /// <summary>
        /// Content type of the request. Default: application/json
        /// </summary>
        [JsonPropertyName("httpContentType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? HttpContentType { get; set; }

        /// <summary>
        /// Format of the log content: JSONLINES, JSONARRAY, or JSONOBJECT.
        /// </summary>
        [JsonPropertyName("httpContentFormat")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? HttpContentFormat { get; set; }

        /// <summary>
        /// Reference to secret containing Authorization header value.
        /// </summary>
        [JsonPropertyName("httpAuthorizationSecretRef")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SecretKeySelector? HttpAuthorizationSecretRef { get; set; }

        /// <summary>
        /// Custom HTTP headers to include in requests.
        /// </summary>
        [JsonPropertyName("httpCustomHeaders")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<HttpHeader>? HttpCustomHeaders { get; set; }
    }

    /// <summary>
    /// Configuration for AWS EventBridge sink.
    /// </summary>
    public class EventBridgeSinkConfig
    {
        /// <summary>
        /// AWS Account ID.
        /// </summary>
        [JsonPropertyName("awsAccountId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AwsAccountId { get; set; }

        /// <summary>
        /// AWS Region (e.g., us-east-1).
        /// </summary>
        [JsonPropertyName("awsRegion")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AwsRegion { get; set; }
    }

    /// <summary>
    /// Configuration for Azure Event Grid sink.
    /// </summary>
    public class EventGridSinkConfig
    {
        /// <summary>
        /// Azure Subscription ID.
        /// </summary>
        [JsonPropertyName("azureSubscriptionId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AzureSubscriptionId { get; set; }

        /// <summary>
        /// Azure Resource Group name.
        /// </summary>
        [JsonPropertyName("azureResourceGroup")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AzureResourceGroup { get; set; }

        /// <summary>
        /// Azure Region.
        /// </summary>
        [JsonPropertyName("azureRegion")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AzureRegion { get; set; }

        /// <summary>
        /// Optional partner topic name.
        /// </summary>
        [JsonPropertyName("azurePartnerTopic")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AzurePartnerTopic { get; set; }
    }

    /// <summary>
    /// Configuration for Datadog sink.
    /// </summary>
    public class DatadogSinkConfig
    {
        /// <summary>
        /// Datadog region: us, eu, us3, us5.
        /// </summary>
        [JsonPropertyName("datadogRegion")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DatadogRegion { get; set; }

        /// <summary>
        /// Reference to secret containing Datadog API key.
        /// </summary>
        [JsonPropertyName("datadogApiKeySecretRef")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SecretKeySelector? DatadogApiKeySecretRef { get; set; }
    }

    /// <summary>
    /// Configuration for Splunk sink.
    /// </summary>
    public class SplunkSinkConfig
    {
        /// <summary>
        /// Splunk domain.
        /// </summary>
        [JsonPropertyName("splunkDomain")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SplunkDomain { get; set; }

        /// <summary>
        /// Splunk port (default: 8088).
        /// </summary>
        [JsonPropertyName("splunkPort")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SplunkPort { get; set; }

        /// <summary>
        /// Reference to secret containing Splunk HEC token.
        /// </summary>
        [JsonPropertyName("splunkTokenSecretRef")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SecretKeySelector? SplunkTokenSecretRef { get; set; }

        /// <summary>
        /// Whether to use HTTPS (default: true).
        /// </summary>
        [JsonPropertyName("splunkSecure")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? SplunkSecure { get; set; }
    }

    /// <summary>
    /// Configuration for Sumo Logic sink.
    /// </summary>
    public class SumoSinkConfig
    {
        /// <summary>
        /// Sumo Logic HTTP source address.
        /// </summary>
        [JsonPropertyName("sumoSourceAddress")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SumoSourceAddress { get; set; }
    }

    /// <summary>
    /// Configuration for Mixpanel sink.
    /// </summary>
    public class MixpanelSinkConfig
    {
        /// <summary>
        /// Mixpanel region: us or eu.
        /// </summary>
        [JsonPropertyName("mixpanelRegion")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MixpanelRegion { get; set; }

        /// <summary>
        /// Mixpanel Project ID.
        /// </summary>
        [JsonPropertyName("mixpanelProjectId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MixpanelProjectId { get; set; }

        /// <summary>
        /// Reference to secret containing Mixpanel service account credentials.
        /// </summary>
        [JsonPropertyName("mixpanelServiceAccountSecretRef")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SecretKeySelector? MixpanelServiceAccountSecretRef { get; set; }
    }

    /// <summary>
    /// Configuration for Segment sink.
    /// </summary>
    public class SegmentSinkConfig
    {
        /// <summary>
        /// Reference to secret containing Segment write key.
        /// </summary>
        [JsonPropertyName("segmentWriteKeySecretRef")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SecretKeySelector? SegmentWriteKeySecretRef { get; set; }
    }
}
