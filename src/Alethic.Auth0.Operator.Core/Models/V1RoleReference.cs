using System.Text.Json.Serialization;

using KubeOps.Abstractions.Entities.Attributes;

namespace Alethic.Auth0.Operator.Core.Models
{

    /// <summary>
    /// Reference to a V1Role resource.
    /// </summary>
    public class V1RoleReference
    {

        /// <summary>
        /// The namespace of the referenced Role. If not specified, defaults to the same namespace as the referencing resource.
        /// </summary>
        [JsonPropertyName("namespace")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Namespace { get; set; }

        /// <summary>
        /// The name of the referenced Role resource.
        /// </summary>
        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [Required]
        public string? Name { get; set; }

        /// <summary>
        /// The Auth0 Role ID. If specified, this takes precedence over name-based lookup.
        /// </summary>
        [JsonPropertyName("id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Id { get; set; }

        /// <inheritdoc />
        public override string ToString()
        {
            return $"{Namespace}/{Name}";
        }

    }

}
