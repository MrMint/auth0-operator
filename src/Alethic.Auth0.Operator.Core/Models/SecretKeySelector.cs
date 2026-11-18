using System.Text.Json.Serialization;

namespace Alethic.Auth0.Operator.Core.Models
{
    /// <summary>
    /// Reference to a Kubernetes Secret key for sensitive values.
    /// Used to securely reference credentials, tokens, API keys, and other
    /// sensitive configuration values stored in Kubernetes Secrets.
    /// </summary>
    public class SecretKeySelector
    {
        /// <summary>
        /// Name of the Kubernetes Secret containing the sensitive value.
        /// </summary>
        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }

        /// <summary>
        /// Key within the Secret to extract the value from.
        /// </summary>
        [JsonPropertyName("key")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Key { get; set; }
    }
}
