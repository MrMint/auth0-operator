using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Alethic.Auth0.Operator.Core.Models.RolePermission
{

    /// <summary>
    /// Configuration for Auth0 Role Permissions.
    /// Represents a set of permissions from a resource server to be assigned to a role.
    /// </summary>
    public partial class RolePermissionConf
    {

        /// <summary>
        /// List of permission names to assign to the role.
        /// These should match scope values defined on the resource server.
        /// </summary>
        [JsonPropertyName("permissions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Permissions { get; set; }

    }

}
