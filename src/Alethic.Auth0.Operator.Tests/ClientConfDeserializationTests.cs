using System.Text.Json;
using Alethic.Auth0.Operator.Core.Models;
using Alethic.Auth0.Operator.Core.Models.Client;
using Alethic.Auth0.Operator.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Alethic.Auth0.Operator.Tests
{
    [TestClass]
    public sealed class ClientConfDeserializationTests
    {
        /// <summary>
        /// Tests that enabled_connections deserializes correctly from snake_case JSON.
        /// This mimics how Kubernetes sends the CR spec to the operator.
        /// </summary>
        [TestMethod]
        public void EnabledConnections_DeserializesFromSnakeCase()
        {
            // This is the JSON that Kubernetes would send, using snake_case property names
            var json = @"{
                ""name"": ""test-client"",
                ""app_type"": ""regular_web"",
                ""enabled_connections"": [
                    {
                        ""name"": ""my-connection"",
                        ""namespace"": ""my-namespace""
                    },
                    {
                        ""name"": ""another-connection"",
                        ""namespace"": ""other-namespace"",
                        ""id"": ""con_12345""
                    }
                ]
            }";

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var conf = JsonSerializer.Deserialize<ClientConf>(json, options);

            Assert.IsNotNull(conf, "ClientConf should not be null");
            Assert.AreEqual("test-client", conf.Name, "Name should be deserialized");
            Assert.IsNotNull(conf.EnabledConnections, "EnabledConnections should not be null");
            Assert.AreEqual(2, conf.EnabledConnections.Length, "EnabledConnections should have 2 entries");

            Assert.AreEqual("my-connection", conf.EnabledConnections[0].Name);
            Assert.AreEqual("my-namespace", conf.EnabledConnections[0].Namespace);
            Assert.IsNull(conf.EnabledConnections[0].Id);

            Assert.AreEqual("another-connection", conf.EnabledConnections[1].Name);
            Assert.AreEqual("other-namespace", conf.EnabledConnections[1].Namespace);
            Assert.AreEqual("con_12345", conf.EnabledConnections[1].Id);
        }

        /// <summary>
        /// Tests that enabled_connections deserializes correctly with default options.
        /// </summary>
        [TestMethod]
        public void EnabledConnections_DeserializesWithDefaultOptions()
        {
            // This is the JSON that Kubernetes would send, using snake_case property names
            var json = @"{
                ""name"": ""test-client"",
                ""enabled_connections"": [
                    {
                        ""name"": ""my-connection"",
                        ""namespace"": ""my-namespace""
                    }
                ]
            }";

            // Use default options - no PropertyNameCaseInsensitive
            var conf = JsonSerializer.Deserialize<ClientConf>(json);

            Assert.IsNotNull(conf, "ClientConf should not be null");
            Assert.IsNotNull(conf.EnabledConnections, "EnabledConnections should not be null with default options");
            Assert.AreEqual(1, conf.EnabledConnections.Length, "EnabledConnections should have 1 entry");
            Assert.AreEqual("my-connection", conf.EnabledConnections[0].Name);
        }

        /// <summary>
        /// Tests that V1ConnectionReference deserializes correctly.
        /// </summary>
        [TestMethod]
        public void V1ConnectionReference_DeserializesCorrectly()
        {
            var json = @"{
                ""name"": ""test-connection"",
                ""namespace"": ""test-namespace"",
                ""id"": ""con_abc123""
            }";

            var connRef = JsonSerializer.Deserialize<V1ConnectionReference>(json);

            Assert.IsNotNull(connRef, "V1ConnectionReference should not be null");
            Assert.AreEqual("test-connection", connRef.Name);
            Assert.AreEqual("test-namespace", connRef.Namespace);
            Assert.AreEqual("con_abc123", connRef.Id);
        }

        /// <summary>
        /// Tests that empty enabled_connections array deserializes correctly.
        /// </summary>
        [TestMethod]
        public void EnabledConnections_EmptyArray_DeserializesCorrectly()
        {
            var json = @"{
                ""name"": ""test-client"",
                ""enabled_connections"": []
            }";

            var conf = JsonSerializer.Deserialize<ClientConf>(json);

            Assert.IsNotNull(conf, "ClientConf should not be null");
            Assert.IsNotNull(conf.EnabledConnections, "EnabledConnections should not be null for empty array");
            Assert.AreEqual(0, conf.EnabledConnections.Length, "EnabledConnections should be empty");
        }

        /// <summary>
        /// Tests that missing enabled_connections results in null.
        /// </summary>
        [TestMethod]
        public void EnabledConnections_Missing_IsNull()
        {
            var json = @"{
                ""name"": ""test-client""
            }";

            var conf = JsonSerializer.Deserialize<ClientConf>(json);

            Assert.IsNotNull(conf, "ClientConf should not be null");
            Assert.IsNull(conf.EnabledConnections, "EnabledConnections should be null when not present in JSON");
        }

        /// <summary>
        /// Tests that the full V1Client.SpecDef structure deserializes correctly.
        /// This mimics the actual structure that Kubernetes sends.
        /// </summary>
        [TestMethod]
        public void V1ClientSpecDef_WithEnabledConnections_DeserializesCorrectly()
        {
            // This mimics the spec portion of a V1Client CR as Kubernetes would send it
            var json = @"{
                ""tenantRef"": {
                    ""name"": ""my-tenant"",
                    ""namespace"": ""auth0-system""
                },
                ""conf"": {
                    ""name"": ""test-client"",
                    ""app_type"": ""regular_web"",
                    ""enabled_connections"": [
                        {
                            ""name"": ""google-workspace"",
                            ""namespace"": ""shared-connections""
                        },
                        {
                            ""name"": ""username-password"",
                            ""namespace"": ""shared-connections""
                        }
                    ]
                }
            }";

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var spec = JsonSerializer.Deserialize<V1Client.SpecDef>(json, options);

            Assert.IsNotNull(spec, "SpecDef should not be null");
            Assert.IsNotNull(spec.TenantRef, "TenantRef should not be null");
            Assert.AreEqual("my-tenant", spec.TenantRef.Name);

            Assert.IsNotNull(spec.Conf, "Conf should not be null");
            Assert.AreEqual("test-client", spec.Conf.Name);

            Assert.IsNotNull(spec.Conf.EnabledConnections, "EnabledConnections should not be null");
            Assert.AreEqual(2, spec.Conf.EnabledConnections.Length, "EnabledConnections should have 2 entries");
            Assert.AreEqual("google-workspace", spec.Conf.EnabledConnections[0].Name);
            Assert.AreEqual("shared-connections", spec.Conf.EnabledConnections[0].Namespace);
        }

        /// <summary>
        /// Tests that the full V1Client structure deserializes correctly.
        /// This is the complete CR structure.
        /// </summary>
        [TestMethod]
        public void V1Client_WithEnabledConnections_DeserializesCorrectly()
        {
            // This mimics a complete V1Client CR as Kubernetes would send it
            var json = @"{
                ""apiVersion"": ""kubernetes.auth0.com/v1"",
                ""kind"": ""Client"",
                ""metadata"": {
                    ""name"": ""my-client"",
                    ""namespace"": ""my-namespace""
                },
                ""spec"": {
                    ""tenantRef"": {
                        ""name"": ""my-tenant"",
                        ""namespace"": ""auth0-system""
                    },
                    ""conf"": {
                        ""name"": ""test-client"",
                        ""app_type"": ""regular_web"",
                        ""enabled_connections"": [
                            {
                                ""name"": ""google-workspace"",
                                ""namespace"": ""shared-connections""
                            }
                        ]
                    }
                }
            }";

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var client = JsonSerializer.Deserialize<V1Client>(json, options);

            Assert.IsNotNull(client, "V1Client should not be null");
            Assert.IsNotNull(client.Spec, "Spec should not be null");
            Assert.IsNotNull(client.Spec.Conf, "Spec.Conf should not be null");
            Assert.IsNotNull(client.Spec.Conf.EnabledConnections, "Spec.Conf.EnabledConnections should not be null");
            Assert.AreEqual(1, client.Spec.Conf.EnabledConnections.Length);
            Assert.AreEqual("google-workspace", client.Spec.Conf.EnabledConnections[0].Name);
        }
    }
}
