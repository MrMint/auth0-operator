using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Alethic.Auth0.Operator.Clients;
using Alethic.Auth0.Operator.Core.Models;
using Alethic.Auth0.Operator.Core.Models.EventStream;
using Alethic.Auth0.Operator.Models;
using Alethic.Auth0.Operator.Options;
using Alethic.Auth0.Operator.RateLimiting;
using Auth0.ManagementApi;
using k8s.Models;
using KubeOps.Abstractions.Controller;
using KubeOps.Abstractions.Queue;
using KubeOps.Abstractions.Rbac;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alethic.Auth0.Operator.Controllers
{
    /// <summary>
    /// Controller for managing Auth0 EventStream resources.
    /// EventStreams provide CloudEvents-compliant user lifecycle events.
    /// Supports Webhook, EventBridge, and Action destinations.
    ///
    /// Note: Uses direct HTTP calls as EventStreams API is in Early Access.
    /// </summary>
    [EntityRbac(typeof(V1Tenant), Verbs = RbacVerb.List | RbacVerb.Get)]
    [EntityRbac(typeof(V1EventStream), Verbs = RbacVerb.All)]
    [EntityRbac(typeof(V1Secret), Verbs = RbacVerb.List | RbacVerb.Get)]
    [EntityRbac(typeof(Eventsv1Event), Verbs = RbacVerb.All)]
    public class V1EventStreamController
        : V1TenantEntityController<
            V1EventStream,
            V1EventStream.SpecDef,
            V1EventStream.StatusDef,
            EventStreamConf
        >,
            IEntityController<V1EventStream>
    {
        /// <summary>
        /// Converts EventStreamType enum to its API string value.
        /// </summary>
        private static string? ToApiString(EventStreamType? type) => type switch
        {
            EventStreamType.EventBridge => "eventbridge",
            EventStreamType.Webhook => "webhook",
            EventStreamType.Action => "action",
            _ => null
        };

        /// <summary>
        /// Converts EventStreamStatus enum to its API string value.
        /// </summary>
        private static string? ToApiString(EventStreamStatus? status) => status switch
        {
            EventStreamStatus.Enabled => "enabled",
            EventStreamStatus.Disabled => "disabled",
            _ => null
        };

        /// <summary>
        /// Converts EventType enum to its API string value.
        /// </summary>
        private static string? ToApiString(EventType? eventType) => eventType switch
        {
            EventType.UserCreated => "user.created",
            EventType.UserUpdated => "user.updated",
            EventType.UserDeleted => "user.deleted",
            EventType.OrganizationCreated => "organization.created",
            EventType.OrganizationUpdated => "organization.updated",
            EventType.OrganizationDeleted => "organization.deleted",
            EventType.OrganizationMemberAdded => "organization.member.added",
            EventType.OrganizationMemberDeleted => "organization.member.deleted",
            EventType.OrganizationMemberRoleAssigned => "organization.member.role.assigned",
            EventType.OrganizationMemberRoleDeleted => "organization.member.role.deleted",
            EventType.OrganizationConnectionAdded => "organization.connection.added",
            EventType.OrganizationConnectionUpdated => "organization.connection.updated",
            EventType.OrganizationConnectionRemoved => "organization.connection.removed",
            _ => null
        };
        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="kube"></param>
        /// <param name="requeue"></param>
        /// <param name="cache"></param>
        /// <param name="logger"></param>
        /// <param name="options"></param>
        /// <param name="clientFactory"></param>
        /// <param name="rateLimiterService"></param>
        /// <param name="reconciliationScheduler"></param>
        public V1EventStreamController(
            IKubernetesClient kube,
            EntityRequeue<V1EventStream> requeue,
            IMemoryCache cache,
            ILogger<V1EventStreamController> logger,
            IOptions<OperatorOptions> options,
            IManagementApiClientFactory clientFactory,
            IRateLimiterService rateLimiterService,
            IReconciliationScheduler reconciliationScheduler
        )
            : base(kube, requeue, cache, logger, options, clientFactory, rateLimiterService, reconciliationScheduler) { }

        /// <inheritdoc />
        protected override string EntityTypeName => "EventStream";

        /// <inheritdoc />
        protected override async Task<Hashtable?> Get(
            IManagementApiClient api,
            string id,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            try
            {
                using var client = CreateEventStreamsClient();
                var result = await client.GetAsync(id, cancellationToken);

                if (result is null)
                    return null;

                return TransformToSystemTextJson<Hashtable>(result);
            }
            catch (EventStreamsApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        /// <inheritdoc />
        protected override async Task<string?> Find(
            IManagementApiClient api,
            V1EventStream entity,
            V1EventStream.SpecDef spec,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            if (spec.Find is not null)
            {
                // If an ID is specified directly, use it
                if (!string.IsNullOrWhiteSpace(spec.Find.Id))
                {
                    Logger.LogInformation(
                        "{EntityTypeName} {EntityNamespace}/{EntityName} using specified ID: {Id}",
                        EntityTypeName,
                        entity.Namespace(),
                        entity.Name(),
                        spec.Find.Id
                    );
                    return spec.Find.Id;
                }

                // Name-based search - list all and find by name
                if (!string.IsNullOrWhiteSpace(spec.Find.Name))
                {
                    Logger.LogDebug(
                        "{EntityTypeName} {EntityNamespace}/{EntityName} searching for event stream by name: {Name}",
                        EntityTypeName,
                        entity.Namespace(),
                        entity.Name(),
                        spec.Find.Name
                    );

                    using var client = CreateEventStreamsClient();
                    var streams = await client.GetAllAsync(cancellationToken);
                    var match = streams.FirstOrDefault(s => s.Name == spec.Find.Name);

                    if (match?.Id is not null)
                    {
                        Logger.LogInformation(
                            "{EntityTypeName} {EntityNamespace}/{EntityName} found event stream by name: {Id}",
                            EntityTypeName,
                            entity.Namespace(),
                            entity.Name(),
                            match.Id
                        );
                        return match.Id;
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        protected override string? ValidateCreate(EventStreamConf conf)
        {
            if (string.IsNullOrWhiteSpace(conf.Name))
                return "missing a value for name";

            if (conf.Type is null)
                return "missing a value for type (webhook, eventbridge, or action)";

            if (conf.Sink is null)
                return "missing sink configuration";

            if (conf.Type == EventStreamType.EventBridge && conf.Sink.EventBridge is null)
                return "missing eventBridge sink configuration";

            if (conf.Type == EventStreamType.Webhook && conf.Sink.Webhook is null)
                return "missing webhook sink configuration";

            return null;
        }

        /// <inheritdoc />
        protected override async Task<string> Create(
            IManagementApiClient api,
            EventStreamConf conf,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            var createRequest = await BuildCreateRequest(conf, defaultNamespace, cancellationToken);

            using var client = CreateEventStreamsClient();
            var result = await client.CreateAsync(createRequest, cancellationToken);

            return result.Id
                ?? throw new InvalidOperationException("Event stream created but no ID returned");
        }

        /// <inheritdoc />
        protected override async Task Update(
            IManagementApiClient api,
            string id,
            Hashtable? last,
            EventStreamConf conf,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            var updateRequest = await BuildUpdateRequest(conf, defaultNamespace, cancellationToken);

            using var client = CreateEventStreamsClient();
            await client.UpdateAsync(id, updateRequest, cancellationToken);
        }

        /// <inheritdoc />
        protected override async Task ApplyStatus(
            IManagementApiClient api,
            V1EventStream entity,
            Hashtable lastConf,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            entity.Status.CurrentStatus = lastConf["status"]?.ToString();

            // Extract destination type and configuration
            // Handle various deserialization types (Hashtable, JsonElement, IDictionary)
            var destination = GetNestedValue(lastConf, "destination");
            if (destination is not null)
            {
                entity.Status.Type = GetStringValue(destination, "type");

                // Extract AWS Partner Event Source for EventBridge destinations
                var configuration = GetNestedValue(destination, "configuration");
                if (configuration is not null)
                {
                    var partnerEventSource = GetStringValue(configuration, "aws_partner_event_source");
                    if (!string.IsNullOrEmpty(partnerEventSource))
                    {
                        entity.Status.AwsPartnerEventSource = partnerEventSource;
                        Logger.LogDebug(
                            "{EntityTypeName} {EntityNamespace}/{EntityName} extracted awsPartnerEventSource: {PartnerEventSource}",
                            EntityTypeName,
                            entity.Namespace(),
                            entity.Name(),
                            partnerEventSource
                        );
                    }
                }
            }

            // Extract subscribed events
            if (lastConf["subscriptions"] is IEnumerable<object> subscriptions)
            {
                entity.Status.SubscribedEvents = subscriptions
                    .Select(s => GetStringValue(s, "event_type"))
                    .Where(e => e is not null)
                    .Cast<string>()
                    .ToList();
            }
            else if (lastConf["subscriptions"] is System.Text.Json.JsonElement subsElement &&
                     subsElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                entity.Status.SubscribedEvents = subsElement
                    .EnumerateArray()
                    .Select(s => s.TryGetProperty("event_type", out var et) ? et.GetString() : null)
                    .Where(e => e is not null)
                    .Cast<string>()
                    .ToList();
            }

            await base.ApplyStatus(api, entity, lastConf, defaultNamespace, cancellationToken);
        }

        /// <summary>
        /// Gets a nested object value from various container types.
        /// </summary>
        private static object? GetNestedValue(object? container, string key)
        {
            return container switch
            {
                Hashtable ht => ht[key],
                IDictionary<string, object> dict => dict.TryGetValue(key, out var val) ? val : null,
                System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Object =>
                    je.TryGetProperty(key, out var prop) ? (object)prop : null,
                _ => null
            };
        }

        /// <summary>
        /// Gets a string value from various container types.
        /// </summary>
        private static string? GetStringValue(object? container, string key)
        {
            var value = GetNestedValue(container, key);
            return value switch
            {
                string s => s,
                System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.String => je.GetString(),
                _ => value?.ToString()
            };
        }

        /// <inheritdoc />
        protected override async Task Delete(
            IManagementApiClient api,
            string id,
            CancellationToken cancellationToken
        )
        {
            using var client = CreateEventStreamsClient();
            await client.DeleteAsync(id, cancellationToken);
        }

        /// <summary>
        /// Creates an EventStreamsClient using the current tenant API context.
        /// Uses the CurrentApiContext property from the base class which is set during
        /// Reconcile and DeletedAsync operations.
        /// </summary>
        private EventStreamsClient CreateEventStreamsClient()
        {
            var context = CurrentApiContext;
            if (context is null)
                throw new InvalidOperationException(
                    "No tenant API context available. This method must be called during Reconcile or Delete operations."
                );

            return new EventStreamsClient(context.Token, context.BaseUri);
        }

        /// <summary>
        /// Builds a create request from the EventStreamConf.
        /// </summary>
        private async Task<EventStreamCreateRequest> BuildCreateRequest(
            EventStreamConf conf,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            var request = new EventStreamCreateRequest
            {
                Name = conf.Name,
                Status = ToApiString(conf.Status) ?? "enabled",
                Subscriptions = conf
                    .Subscriptions?.Select(s => new EventStreamSubscriptionRequest
                    {
                        EventType = ToApiString(s.EventType),
                    })
                    .ToList(),
            };

            request.Destination = await BuildDestination(conf, defaultNamespace, cancellationToken);

            return request;
        }

        /// <summary>
        /// Builds an update request from the EventStreamConf.
        /// </summary>
        private async Task<EventStreamUpdateRequest> BuildUpdateRequest(
            EventStreamConf conf,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            var request = new EventStreamUpdateRequest
            {
                Name = conf.Name,
                Status = ToApiString(conf.Status),
                Subscriptions = conf
                    .Subscriptions?.Select(s => new EventStreamSubscriptionRequest
                    {
                        EventType = ToApiString(s.EventType),
                    })
                    .ToList(),
            };

            request.Destination = await BuildDestination(conf, defaultNamespace, cancellationToken);

            return request;
        }

        /// <summary>
        /// Builds the destination configuration.
        /// </summary>
        private async Task<EventStreamDestination> BuildDestination(
            EventStreamConf conf,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            var destination = new EventStreamDestination
            {
                Type = ToApiString(conf.Type),
                Configuration = new EventStreamDestinationConfiguration(),
            };

            switch (conf.Type)
            {
                case EventStreamType.Webhook:
                    if (conf.Sink?.Webhook is { } webhook)
                    {
                        destination.Configuration.WebhookEndpoint = webhook.Url;

                        // Resolve authorization from secret if specified
                        if (webhook.AuthorizationSecretRef is { } authSecretRef)
                        {
                            var authValue = await ResolveSecretValue(
                                authSecretRef,
                                defaultNamespace,
                                cancellationToken
                            );
                            if (!string.IsNullOrEmpty(authValue))
                            {
                                // Parse the authorization value - could be "Bearer <token>" or "Basic <credentials>"
                                destination.Configuration.WebhookAuthorization =
                                    new List<EventStreamWebhookAuthorization>();

                                if (
                                    authValue.StartsWith(
                                        "Bearer ",
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                )
                                {
                                    destination.Configuration.WebhookAuthorization.Add(
                                        new EventStreamWebhookAuthorization
                                        {
                                            Method = "bearer",
                                            Token = authValue.Substring(7),
                                        }
                                    );
                                }
                                else if (
                                    authValue.StartsWith(
                                        "Basic ",
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                )
                                {
                                    // Decode Basic auth to get username:password
                                    var credentials = Encoding.UTF8.GetString(
                                        Convert.FromBase64String(authValue.Substring(6))
                                    );
                                    var parts = credentials.Split(':', 2);
                                    destination.Configuration.WebhookAuthorization.Add(
                                        new EventStreamWebhookAuthorization
                                        {
                                            Method = "basic",
                                            Username = parts.Length > 0 ? parts[0] : null,
                                            Password = parts.Length > 1 ? parts[1] : null,
                                        }
                                    );
                                }
                                else
                                {
                                    // Assume it's a raw bearer token
                                    destination.Configuration.WebhookAuthorization.Add(
                                        new EventStreamWebhookAuthorization
                                        {
                                            Method = "bearer",
                                            Token = authValue,
                                        }
                                    );
                                }
                            }
                        }
                    }
                    break;

                case EventStreamType.EventBridge:
                    if (conf.Sink?.EventBridge is { } eventBridge)
                    {
                        destination.Configuration.AwsAccountId = eventBridge.AwsAccountId;
                        destination.Configuration.AwsRegion = eventBridge.AwsRegion;
                    }
                    break;

                case EventStreamType.Action:
                    // Action destination would need action_id configuration
                    // This is typically used for Auth0 Actions integration
                    break;
            }

            return destination;
        }

        /// <summary>
        /// Resolves a secret value from Kubernetes.
        /// </summary>
        private async Task<string?> ResolveSecretValue(
            SecretKeySelector secretRef,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            if (string.IsNullOrWhiteSpace(secretRef.Name))
                return null;

            var secret = await Kube.GetAsync<V1Secret>(
                secretRef.Name,
                defaultNamespace,
                cancellationToken
            );
            if (secret?.Data is null)
            {
                Logger.LogWarning(
                    "Secret {SecretName} not found in namespace {Namespace}",
                    secretRef.Name,
                    defaultNamespace
                );
                return null;
            }

            var key = secretRef.Key ?? "value";
            if (!secret.Data.TryGetValue(key, out var valueBytes))
            {
                Logger.LogWarning(
                    "Key {Key} not found in secret {SecretName}",
                    key,
                    secretRef.Name
                );
                return null;
            }

            return Encoding.UTF8.GetString(valueBytes);
        }
    }
}
