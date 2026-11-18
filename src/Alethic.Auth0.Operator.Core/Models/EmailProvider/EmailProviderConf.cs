using System.Collections;
using System.Text.Json.Serialization;

using Alethic.Auth0.Operator.Core.Extensions;

namespace Alethic.Auth0.Operator.Core.Models.EmailProvider
{

    /// <summary>
    /// A single credential value. Exactly one of <see cref="Value"/> or <see cref="SecretKeyRef"/> must be set.
    /// </summary>
    public class EmailProviderCredentialValue
    {

        /// <summary>
        /// Literal value. Prefer <see cref="SecretKeyRef"/> for anything sensitive.
        /// </summary>
        [JsonPropertyName("value")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Value { get; set; }

        /// <summary>
        /// Reference to a key in a Secret in the same namespace as the resource.
        /// </summary>
        [JsonPropertyName("secretKeyRef")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SecretKeySelector? SecretKeyRef { get; set; }

    }

    /// <summary>
    /// Credentials for the email provider. Which fields apply depends on the provider name.
    /// Auth0 never returns these values, so they are sent on every create and update.
    /// </summary>
    public class EmailProviderCredentialsConf
    {

        /// <summary>
        /// API user (legacy providers).
        /// </summary>
        [JsonPropertyName("api_user")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public EmailProviderCredentialValue? ApiUser { get; set; }

        /// <summary>
        /// API key (mandrill, sendgrid, sparkpost, mailgun).
        /// </summary>
        [JsonPropertyName("api_key")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public EmailProviderCredentialValue? ApiKey { get; set; }

        /// <summary>
        /// AWS access key ID (ses).
        /// </summary>
        [JsonPropertyName("accessKeyId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public EmailProviderCredentialValue? AccessKeyId { get; set; }

        /// <summary>
        /// AWS secret access key (ses).
        /// </summary>
        [JsonPropertyName("secretAccessKey")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public EmailProviderCredentialValue? SecretAccessKey { get; set; }

        /// <summary>
        /// Region (ses, sparkpost, mailgun).
        /// </summary>
        [JsonPropertyName("region")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public EmailProviderCredentialValue? Region { get; set; }

        /// <summary>
        /// Domain (mailgun).
        /// </summary>
        [JsonPropertyName("domain")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public EmailProviderCredentialValue? Domain { get; set; }

        /// <summary>
        /// SMTP host (smtp).
        /// </summary>
        [JsonPropertyName("smtp_host")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public EmailProviderCredentialValue? SmtpHost { get; set; }

        /// <summary>
        /// SMTP port (smtp). Must resolve to an integer.
        /// </summary>
        [JsonPropertyName("smtp_port")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public EmailProviderCredentialValue? SmtpPort { get; set; }

        /// <summary>
        /// SMTP user name (smtp).
        /// </summary>
        [JsonPropertyName("smtp_user")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public EmailProviderCredentialValue? SmtpUser { get; set; }

        /// <summary>
        /// SMTP password (smtp).
        /// </summary>
        [JsonPropertyName("smtp_pass")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public EmailProviderCredentialValue? SmtpPass { get; set; }

        /// <summary>
        /// Connection string (azure_cs).
        /// </summary>
        [JsonPropertyName("connectionString")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public EmailProviderCredentialValue? ConnectionString { get; set; }

        /// <summary>
        /// Directory tenant ID (ms365).
        /// </summary>
        [JsonPropertyName("tenantId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public EmailProviderCredentialValue? TenantId { get; set; }

        /// <summary>
        /// Application client ID (ms365).
        /// </summary>
        [JsonPropertyName("clientId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public EmailProviderCredentialValue? ClientId { get; set; }

        /// <summary>
        /// Application client secret (ms365).
        /// </summary>
        [JsonPropertyName("clientSecret")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public EmailProviderCredentialValue? ClientSecret { get; set; }

    }

    /// <summary>
    /// Configuration for the tenant email provider, matching <c>/api/v2/emails/provider</c>.
    /// </summary>
    public class EmailProviderConf
    {

        /// <summary>
        /// Provider name as accepted by Auth0, for example ses, smtp, sendgrid, mailgun, mandrill,
        /// sparkpost, azure_cs, ms365 or custom.
        /// </summary>
        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }

        /// <summary>
        /// Whether the provider is enabled.
        /// </summary>
        [JsonPropertyName("enabled")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Enabled { get; set; }

        /// <summary>
        /// Email address used as the sender when a template does not set one.
        /// </summary>
        [JsonPropertyName("default_from_address")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DefaultFromAddress { get; set; }

        /// <summary>
        /// Provider credentials. Each value is either a literal or a reference to a Secret key.
        /// </summary>
        [JsonPropertyName("credentials")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public EmailProviderCredentialsConf? Credentials { get; set; }

        /// <summary>
        /// Provider-specific settings, passed through to Auth0 unchanged.
        /// </summary>
        [JsonPropertyName("settings")]
        [JsonConverter(typeof(SimplePrimitiveHashtableConverter))]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Hashtable? Settings { get; set; }

    }

}
