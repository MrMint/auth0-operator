using System.Collections;
using System.Text.Json.Serialization;

using Alethic.Auth0.Operator.Core.Extensions;

namespace Alethic.Auth0.Operator.Core.Models.Connection
{

    public class ConnectionConf
    {

        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }

        [JsonPropertyName("display_name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DisplayName { get; set; }

        [JsonPropertyName("strategy")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Strategy { get; set; }

        [JsonPropertyName("options")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonConverter(typeof(SimplePrimitiveHashtableConverter))]
        public Hashtable? Options { get; set; }

        [JsonPropertyName("provisioning_ticket_url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ProvisioningTicketUrl { get; set; }

        [JsonPropertyName("metadata")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonConverter(typeof(SimplePrimitiveHashtableConverter))]
        public Hashtable? Metadata { get; set; }

        [JsonPropertyName("realms")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string[]? Realms { get; set; }

        // Note: enabled_clients is intentionally NOT exposed here.
        // It is computed dynamically by the Connection controller via aggregation
        // from all Client CRDs that reference this connection in their enabledConnections.
        // This prevents race conditions where both Client and Connection controllers
        // try to manage the same Auth0 state.

        [JsonPropertyName("show_as_button")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? ShowAsButton { get; set; }

        [JsonPropertyName("is_domain_connection")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IsDomainConnection { get; set; } = false;

    }

}
