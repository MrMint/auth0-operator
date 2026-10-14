using System.Text.Json.Serialization;
using KubeOps.Abstractions.Entities.Attributes;

namespace Alethic.Auth0.Operator.Core.Models
{
    /// <summary>
    /// Reference to a V1ResourceServer resource.
    /// </summary>
    public class V1ResourceServerReference
    {
        /// <summary>
        /// The namespace of the referenced ResourceServer. If not specified, defaults to the same namespace as the referencing resource.
        /// </summary>
        [JsonPropertyName("namespace")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Namespace { get; set; }

        /// <summary>
        /// The name of the referenced ResourceServer resource.
        /// </summary>
        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [Required]
        public string? Name { get; set; }

        /// <summary>
        /// The Auth0 Resource Server ID (format: rs_XXXXX). Use this for direct ID-based lookup
        /// when you have the Auth0 ID but not a corresponding Kubernetes resource.
        /// Takes precedence over Name-based lookup but not over Identifier.
        /// </summary>
        [JsonPropertyName("id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Id { get; set; }

        /// <summary>
        /// The Resource Server identifier (audience), e.g., "https://api.example.com".
        /// Use this when you know the audience URI directly. This takes highest precedence
        /// and avoids any lookup - the value is used as-is for API operations.
        /// </summary>
        [JsonPropertyName("identifier")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Identifier { get; set; }

        /// <inheritdoc />
        public override string ToString()
        {
            if (Id is not null)
                return Id;
            else
                return $"{Namespace}/{Name}";
        }
    }
}
