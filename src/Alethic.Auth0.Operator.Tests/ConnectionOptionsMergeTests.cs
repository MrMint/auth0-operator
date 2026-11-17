using System;
using System.Collections;
using System.Reflection;
using System.Text.Json;

using Alethic.Auth0.Operator.Controllers;
using Alethic.Auth0.Operator.Core.Extensions;

using Auth0.ManagementApi.Models;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Alethic.Auth0.Operator.Tests
{

    /// <summary>
    /// Tests the options <see cref="V1ConnectionController"/> sends. Auth0 replaces the whole
    /// options object on <c>PATCH /api/v2/connections/{id}</c>, so anything missing from the
    /// body is wiped: the spec's keys must be laid over the live options. On create and update,
    /// options the typed model doesn't know must reach Auth0.
    /// </summary>
    [TestClass]
    public sealed class ConnectionOptionsMergeTests
    {

        static readonly MethodInfo MergeOptionsMethod =
            typeof(V1ConnectionController).GetMethod(
                "MergeOptions",
                BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new InvalidOperationException("Could not locate MergeOptions via reflection.");

        static readonly MethodInfo CreateOptionsMethod =
            typeof(V1ConnectionController).GetMethod(
                "CreateOptions",
                BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new InvalidOperationException("Could not locate CreateOptions via reflection.");

        /// <summary>
        /// A database connection's options as Auth0 returns them, including fields the typed
        /// model doesn't know (<c>brute_force_protection</c>, <c>disable_signup</c>,
        /// <c>mfa</c>, <c>strategy_version</c>).
        /// </summary>
        const string LiveOptionsJson = """
            {
                "passwordPolicy": "good",
                "password_complexity_options": { "min_length": 12 },
                "password_history": { "enable": true, "size": 5 },
                "password_dictionary": { "enable": false, "dictionary": [] },
                "password_no_personal_info": { "enable": true },
                "brute_force_protection": true,
                "disable_signup": false,
                "mfa": { "active": true, "return_enroll_settings": true },
                "strategy_version": 2,
                "requires_username": false,
                "authentication_methods": {
                    "password": { "enabled": true },
                    "passkey": { "enabled": false }
                }
            }
            """;

        /// <summary>
        /// Parses options the way Get() hands them to Update(): a Hashtable read through the
        /// operator's converter.
        /// </summary>
        static Hashtable ReadOptions(string json)
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new SimplePrimitiveHashtableConverter() },
            };
            return System.Text.Json.JsonSerializer.Deserialize<Hashtable>(json, options)
                ?? throw new InvalidOperationException();
        }

        static object? Invoke(MethodInfo method, params object?[] args)
        {
            try
            {
                return method.Invoke(null, args);
            }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            {
                throw e.InnerException;
            }
        }

        static JObject? MergeOptions(string strategy, Hashtable? live, Hashtable? spec) =>
            (JObject?)Invoke(MergeOptionsMethod, strategy, live, spec);

        static JObject CreateOptions(string strategy, Hashtable? options) =>
            (JObject)Invoke(CreateOptionsMethod, strategy, options)!;

        /// <summary>
        /// The options object of a request body, serialized the way the Management API client
        /// serializes the request.
        /// </summary>
        static JToken? BodyOptions(object request)
        {
            var body = JsonConvert.SerializeObject(request);
            var settings = new JsonSerializerSettings { DateParseHandling = DateParseHandling.None };
            return JsonConvert.DeserializeObject<JObject>(body, settings)?["options"];
        }

        static JToken? PatchBodyOptions(JObject? options) =>
            BodyOptions(new ConnectionUpdateRequest { Options = options! });

        static JToken? CreateBodyOptions(JObject options) =>
            BodyOptions(new ConnectionCreateRequest { Options = options });

        [TestMethod]
        public void SpecOptionChangesOnlyThatKeyInThePatchBody()
        {
            var live = ReadOptions(LiveOptionsJson);
            var spec = new Hashtable { ["disable_signup"] = true };

            var sent = PatchBodyOptions(MergeOptions("auth0", live, spec));

            var expected = JObject.Parse(LiveOptionsJson);
            expected["disable_signup"] = true;
            Assert.IsTrue(
                JToken.DeepEquals(expected, sent),
                $"Expected:{Environment.NewLine}{expected}{Environment.NewLine}Sent:{Environment.NewLine}{sent}");
        }

        [TestMethod]
        public void SpecValueReplacesALiveTypedOption()
        {
            var live = ReadOptions(LiveOptionsJson);
            var spec = new Hashtable { ["passwordPolicy"] = "excellent" };

            var sent = PatchBodyOptions(MergeOptions("auth0", live, spec));

            var expected = JObject.Parse(LiveOptionsJson);
            expected["passwordPolicy"] = "excellent";
            Assert.IsTrue(JToken.DeepEquals(expected, sent), sent?.ToString());
        }

        [TestMethod]
        public void NoSpecOptionsLeavesOptionsOutOfThePatch()
        {
            var live = ReadOptions(LiveOptionsJson);

            Assert.IsNull(MergeOptions("auth0", live, null));
        }

        [TestMethod]
        public void ADateLikeStringGoesBackUnchanged()
        {
            var live = ReadOptions("""{ "note": "2026-11-17T10:00:00-06:00" }""");
            var spec = new Hashtable { ["disable_signup"] = true };

            var sent = PatchBodyOptions(MergeOptions("auth0", live, spec));

            Assert.AreEqual(JTokenType.String, sent?["note"]?.Type);
            Assert.AreEqual("2026-11-17T10:00:00-06:00", sent?["note"]?.Value<string>());
        }

        [TestMethod]
        public void MistypedDatabaseOptionFailsTheReconcile()
        {
            var live = ReadOptions(LiveOptionsJson);
            var spec = new Hashtable { ["passwordPolicy"] = "medium" };

            Assert.ThrowsException<System.Text.Json.JsonException>(() => MergeOptions("auth0", live, spec));
        }

        [TestMethod]
        public void CreateSendsTheSpecOptionsAsWritten()
        {
            const string specJson = """
                {
                    "passwordPolicy": "good",
                    "brute_force_protection": true,
                    "disable_signup": true
                }
                """;

            var sent = CreateBodyOptions(CreateOptions("auth0", ReadOptions(specJson)));

            var expected = JObject.Parse(specJson);
            Assert.IsTrue(JToken.DeepEquals(expected, sent), sent?.ToString());
        }

        [TestMethod]
        public void CreateFailsOnAMistypedDatabaseOption()
        {
            var spec = new Hashtable { ["passwordPolicy"] = "medium" };

            Assert.ThrowsException<System.Text.Json.JsonException>(() => CreateOptions("auth0", spec));
        }

        [TestMethod]
        public void CreateFailsWithoutOptions()
        {
            Assert.ThrowsException<InvalidOperationException>(() => CreateOptions("auth0", null));
        }

    }

}
