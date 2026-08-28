using System.Text.Json.Serialization;

namespace Alethic.Auth0.Operator.Core.Models.Role
{

    /// <summary>
    /// Configuration for an Auth0 Role matching the Auth0 Management API schema.
    /// </summary>
    public partial class RoleConf
    {

        /// <summary>
        /// The ID of the role.
        /// </summary>
        [JsonPropertyName("id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Id { get; set; }

        /// <summary>
        /// The name of this role.
        /// </summary>
        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }

        /// <summary>
        /// A description of the role.
        /// </summary>
        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Description { get; set; }

    }

}
