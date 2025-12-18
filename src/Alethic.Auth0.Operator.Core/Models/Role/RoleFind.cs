using System.Text.Json.Serialization;

namespace Alethic.Auth0.Operator.Core.Models.Role
{

    /// <summary>
    /// Criteria for finding an existing Auth0 Role to adopt.
    /// </summary>
    public class RoleFind
    {

        /// <summary>
        /// Filter roles by ID (exact match).
        /// </summary>
        [JsonPropertyName("id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Id { get; set; }

        /// <summary>
        /// Filter roles by name (exact match).
        /// </summary>
        [JsonPropertyName("nameFilter")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? NameFilter { get; set; }

    }

}
