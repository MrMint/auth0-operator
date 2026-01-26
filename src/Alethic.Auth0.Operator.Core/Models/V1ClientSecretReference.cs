using System.Text.Json.Serialization;

namespace Alethic.Auth0.Operator.Core.Models
{

    /// <summary>
    /// Defines a reference to a Kubernetes Secret for storing client credentials.
    /// </summary>
    public class V1ClientSecretReference
    {

        /// <summary>
        /// The name of the Secret.
        /// </summary>
        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }

        /// <summary>
        /// The namespace of the Secret. If not specified, the namespace of the Client resource is used.
        /// </summary>
        [JsonPropertyName("namespace")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? NamespaceProperty { get; set; }

        /// <summary>
        /// The format of the secret output. When set to "json", an additional key containing
        /// a JSON object with clientId and clientSecret will be added to the secret.
        /// Valid values: null (default - only separate keys), "json" (include JSON key).
        /// </summary>
        [JsonPropertyName("format")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Format { get; set; }

        /// <summary>
        /// The name of the key to use for the JSON-formatted credentials when format is "json".
        /// Defaults to "credentials" if not specified.
        /// </summary>
        [JsonPropertyName("jsonKey")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? JsonKey { get; set; }

        /// <inheritdoc />
        public override string ToString()
        {
            return $"{NamespaceProperty}/{Name}";
        }

    }

}
