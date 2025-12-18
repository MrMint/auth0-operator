using System;
using System.Collections;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Alethic.Auth0.Operator.Core.Models;
using Alethic.Auth0.Operator.Core.Models.LogStream;
using Alethic.Auth0.Operator.Models;
using Alethic.Auth0.Operator.Options;
using Auth0.Core.Exceptions;
using Auth0.ManagementApi;
using Auth0.ManagementApi.Models;
using k8s.Models;
using KubeOps.Abstractions.Controller;
using KubeOps.Abstractions.Queue;
using KubeOps.Abstractions.Rbac;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Auth0LogStreamType = Auth0.ManagementApi.Models.LogStreamType;

namespace Alethic.Auth0.Operator.Controllers
{
    /// <summary>
    /// Controller for managing Auth0 LogStream resources.
    /// Supports 8 destination types: HTTP, EventBridge, EventGrid, Datadog, Splunk, Sumo, Mixpanel, Segment.
    /// </summary>
    [EntityRbac(typeof(V1Tenant), Verbs = RbacVerb.List | RbacVerb.Get)]
    [EntityRbac(typeof(V1LogStream), Verbs = RbacVerb.All)]
    [EntityRbac(typeof(V1Secret), Verbs = RbacVerb.List | RbacVerb.Get)]
    [EntityRbac(typeof(Eventsv1Event), Verbs = RbacVerb.All)]
    public class V1LogStreamController
        : V1TenantEntityController<
            V1LogStream,
            V1LogStream.SpecDef,
            V1LogStream.StatusDef,
            LogStreamConf
        >,
            IEntityController<V1LogStream>
    {
        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="kube"></param>
        /// <param name="requeue"></param>
        /// <param name="cache"></param>
        /// <param name="logger"></param>
        /// <param name="options"></param>
        public V1LogStreamController(
            IKubernetesClient kube,
            EntityRequeue<V1LogStream> requeue,
            IMemoryCache cache,
            ILogger<V1LogStreamController> logger,
            IOptions<OperatorOptions> options
        )
            : base(kube, requeue, cache, logger, options) { }

        /// <inheritdoc />
        protected override string EntityTypeName => "LogStream";

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
                return TransformToSystemTextJson<Hashtable>(
                    await api.LogStreams.GetAsync(id, cancellationToken)
                );
            }
            catch (ErrorApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        /// <inheritdoc />
        protected override async Task<string?> Find(
            IManagementApiClient api,
            V1LogStream entity,
            V1LogStream.SpecDef spec,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            if (spec.Find is not null)
            {
                // If an ID is specified directly, try to find by ID
                if (spec.Find.Id is string logStreamId)
                {
                    try
                    {
                        var logStream = await api.LogStreams.GetAsync(
                            logStreamId,
                            cancellationToken
                        );
                        Logger.LogInformation(
                            "{EntityTypeName} {EntityNamespace}/{EntityName} found existing log stream by ID: {Name}",
                            EntityTypeName,
                            entity.Namespace(),
                            entity.Name(),
                            logStream.Name
                        );
                        return logStream.Id;
                    }
                    catch (ErrorApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
                    {
                        Logger.LogInformation(
                            "{EntityTypeName} {EntityNamespace}/{EntityName} could not find log stream with id {LogStreamId}.",
                            EntityTypeName,
                            entity.Namespace(),
                            entity.Name(),
                            logStreamId
                        );
                        return null;
                    }
                }

                // If a name is specified, search by name
                if (spec.Find.Name is string nameFilter)
                {
                    var logStreams = await api.LogStreams.GetAllAsync(cancellationToken);
                    var logStream = logStreams.FirstOrDefault(ls => ls.Name == nameFilter);
                    if (logStream is not null)
                    {
                        Logger.LogInformation(
                            "{EntityTypeName} {EntityNamespace}/{EntityName} found existing log stream by name: {Name}",
                            EntityTypeName,
                            entity.Namespace(),
                            entity.Name(),
                            logStream.Name
                        );
                        return logStream.Id;
                    }
                    Logger.LogInformation(
                        "{EntityTypeName} {EntityNamespace}/{EntityName} could not find log stream with name {Name}.",
                        EntityTypeName,
                        entity.Namespace(),
                        entity.Name(),
                        nameFilter
                    );
                    return null;
                }

                return null;
            }
            else
            {
                // Default: search by name from conf
                var conf = spec.Init ?? spec.Conf;
                if (conf is null)
                    return null;

                if (string.IsNullOrWhiteSpace(conf.Name))
                    return null;

                var logStreams = await api.LogStreams.GetAllAsync(cancellationToken);
                var self = logStreams.FirstOrDefault(ls => ls.Name == conf.Name);
                return self?.Id;
            }
        }

        /// <inheritdoc />
        protected override string? ValidateCreate(LogStreamConf conf)
        {
            if (string.IsNullOrWhiteSpace(conf.Name))
                return "missing a value for name";

            if (string.IsNullOrWhiteSpace(conf.Type))
                return "missing a value for type";

            if (conf.Sink is null)
                return "missing sink configuration";

            return null;
        }

        /// <inheritdoc />
        protected override async Task<string> Create(
            IManagementApiClient api,
            LogStreamConf conf,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            Logger.LogInformation(
                "{EntityTypeName} creating log stream in Auth0 with name: {LogStreamName}, type: {Type}",
                EntityTypeName,
                conf.Name,
                conf.Type
            );

            var request = await BuildLogStreamCreateRequestAsync(
                conf,
                defaultNamespace,
                cancellationToken
            );
            var self = await api.LogStreams.CreateAsync(request, cancellationToken);

            Logger.LogInformation(
                "{EntityTypeName} successfully created log stream in Auth0 with ID: {LogStreamId} and name: {LogStreamName}",
                EntityTypeName,
                self.Id,
                conf.Name
            );
            return self.Id;
        }

        /// <inheritdoc />
        protected override async Task Update(
            IManagementApiClient api,
            string id,
            Hashtable? last,
            LogStreamConf conf,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            Logger.LogInformation(
                "{EntityTypeName} updating log stream in Auth0 with id: {LogStreamId} and name: {LogStreamName}",
                EntityTypeName,
                id,
                conf.Name
            );

            var request = await BuildLogStreamUpdateRequestAsync(
                conf,
                defaultNamespace,
                cancellationToken
            );
            await api.LogStreams.UpdateAsync(id, request, cancellationToken);

            Logger.LogInformation(
                "{EntityTypeName} successfully updated log stream in Auth0 with id: {LogStreamId} and name: {LogStreamName}",
                EntityTypeName,
                id,
                conf.Name
            );
        }

        /// <inheritdoc />
        protected override async Task ApplyStatus(
            IManagementApiClient api,
            V1LogStream entity,
            Hashtable lastConf,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            entity.Status.CurrentStatus = (string?)lastConf["status"];
            entity.Status.Type = (string?)lastConf["type"];
            await base.ApplyStatus(api, entity, lastConf, defaultNamespace, cancellationToken);
        }

        /// <inheritdoc />
        protected override Task Delete(
            IManagementApiClient api,
            string id,
            CancellationToken cancellationToken
        )
        {
            Logger.LogInformation(
                "{EntityTypeName} deleting log stream from Auth0 with ID: {LogStreamId}",
                EntityTypeName,
                id
            );
            return api.LogStreams.DeleteAsync(id, cancellationToken);
        }

        /// <summary>
        /// Builds a LogStreamCreateRequest from the configuration.
        /// </summary>
        private async Task<LogStreamCreateRequest> BuildLogStreamCreateRequestAsync(
            LogStreamConf conf,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            var request = new LogStreamCreateRequest
            {
                Name = conf.Name,
                Type = MapLogStreamType(conf.Type),
            };

            // Build sink configuration based on type
            if (conf.Sink is not null)
            {
                request.Sink = await BuildSinkAsync(
                    conf.Type,
                    conf.Sink,
                    defaultNamespace,
                    cancellationToken
                );
            }

            return request;
        }

        /// <summary>
        /// Builds a LogStreamUpdateRequest from the configuration.
        /// </summary>
        private async Task<LogStreamUpdateRequest> BuildLogStreamUpdateRequestAsync(
            LogStreamConf conf,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            var request = new LogStreamUpdateRequest { Name = conf.Name };

            // Set status if specified
            if (!string.IsNullOrWhiteSpace(conf.Status))
            {
                request.Status = conf.Status switch
                {
                    "active" => LogStreamUpdateStatus.Active,
                    "paused" => LogStreamUpdateStatus.Paused,
                    _ => null,
                };
            }

            // Build sink configuration based on type
            if (conf.Sink is not null && !string.IsNullOrWhiteSpace(conf.Type))
            {
                request.Sink = await BuildSinkAsync(
                    conf.Type,
                    conf.Sink,
                    defaultNamespace,
                    cancellationToken
                );
            }

            return request;
        }

        /// <summary>
        /// Maps the string type to Auth0 LogStreamType.
        /// </summary>
        private static Auth0LogStreamType MapLogStreamType(string? type)
        {
            return type?.ToLowerInvariant() switch
            {
                "http" => Auth0LogStreamType.Http,
                "eventbridge" => Auth0LogStreamType.EventBridge,
                "eventgrid" => Auth0LogStreamType.EventGrid,
                "datadog" => Auth0LogStreamType.Datadog,
                "splunk" => Auth0LogStreamType.Splunk,
                "sumo" => Auth0LogStreamType.Sumo,
                "mixpanel" => Auth0LogStreamType.MixPanel,
                "segment" => Auth0LogStreamType.Segment,
                _ => throw new InvalidOperationException($"Unknown log stream type: {type}"),
            };
        }

        /// <summary>
        /// Builds the sink object based on the log stream type.
        /// </summary>
        private async Task<object> BuildSinkAsync(
            string? type,
            LogStreamSink sink,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            return type?.ToLowerInvariant() switch
            {
                "http" => await BuildHttpSinkAsync(sink.Http, defaultNamespace, cancellationToken),
                "eventbridge" => BuildEventBridgeSink(sink.EventBridge),
                "eventgrid" => BuildEventGridSink(sink.EventGrid),
                "datadog" => await BuildDatadogSinkAsync(
                    sink.Datadog,
                    defaultNamespace,
                    cancellationToken
                ),
                "splunk" => await BuildSplunkSinkAsync(
                    sink.Splunk,
                    defaultNamespace,
                    cancellationToken
                ),
                "sumo" => BuildSumoSink(sink.Sumo),
                "mixpanel" => await BuildMixpanelSinkAsync(
                    sink.Mixpanel,
                    defaultNamespace,
                    cancellationToken
                ),
                "segment" => await BuildSegmentSinkAsync(
                    sink.Segment,
                    defaultNamespace,
                    cancellationToken
                ),
                _ => throw new InvalidOperationException($"Unknown log stream type: {type}"),
            };
        }

        private async Task<object> BuildHttpSinkAsync(
            HttpSinkConfig? config,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            if (config is null)
                throw new InvalidOperationException(
                    "HTTP sink configuration is required for HTTP log streams."
                );

            var result = new
            {
                httpEndpoint = config.HttpEndpoint,
                httpContentType = config.HttpContentType ?? "application/json",
                httpContentFormat = config.HttpContentFormat ?? "JSONLINES",
                httpAuthorization = config.HttpAuthorizationSecretRef is not null
                    ? await ResolveSecretValueAsync(
                        config.HttpAuthorizationSecretRef,
                        defaultNamespace,
                        cancellationToken
                    )
                    : null,
            };

            return result;
        }

        private static object BuildEventBridgeSink(EventBridgeSinkConfig? config)
        {
            if (config is null)
                throw new InvalidOperationException(
                    "EventBridge sink configuration is required for EventBridge log streams."
                );

            return new { awsAccountId = config.AwsAccountId, awsRegion = config.AwsRegion };
        }

        private static object BuildEventGridSink(EventGridSinkConfig? config)
        {
            if (config is null)
                throw new InvalidOperationException(
                    "EventGrid sink configuration is required for EventGrid log streams."
                );

            return new
            {
                azureSubscriptionId = config.AzureSubscriptionId,
                azureResourceGroup = config.AzureResourceGroup,
                azureRegion = config.AzureRegion,
                azurePartnerTopic = config.AzurePartnerTopic,
            };
        }

        private async Task<object> BuildDatadogSinkAsync(
            DatadogSinkConfig? config,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            if (config is null)
                throw new InvalidOperationException(
                    "Datadog sink configuration is required for Datadog log streams."
                );

            return new
            {
                datadogRegion = config.DatadogRegion ?? "us",
                datadogApiKey = config.DatadogApiKeySecretRef is not null
                    ? await ResolveSecretValueAsync(
                        config.DatadogApiKeySecretRef,
                        defaultNamespace,
                        cancellationToken
                    )
                    : null,
            };
        }

        private async Task<object> BuildSplunkSinkAsync(
            SplunkSinkConfig? config,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            if (config is null)
                throw new InvalidOperationException(
                    "Splunk sink configuration is required for Splunk log streams."
                );

            return new
            {
                splunkDomain = config.SplunkDomain,
                splunkPort = config.SplunkPort ?? "8088",
                splunkToken = config.SplunkTokenSecretRef is not null
                    ? await ResolveSecretValueAsync(
                        config.SplunkTokenSecretRef,
                        defaultNamespace,
                        cancellationToken
                    )
                    : null,
                splunkSecure = config.SplunkSecure ?? true,
            };
        }

        private static object BuildSumoSink(SumoSinkConfig? config)
        {
            if (config is null)
                throw new InvalidOperationException(
                    "Sumo Logic sink configuration is required for Sumo log streams."
                );

            return new { sumoSourceAddress = config.SumoSourceAddress };
        }

        private async Task<object> BuildMixpanelSinkAsync(
            MixpanelSinkConfig? config,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            if (config is null)
                throw new InvalidOperationException(
                    "Mixpanel sink configuration is required for Mixpanel log streams."
                );

            return new
            {
                mixpanelRegion = config.MixpanelRegion ?? "us",
                mixpanelProjectId = config.MixpanelProjectId,
                mixpanelServiceAccountUsername = config.MixpanelServiceAccountSecretRef is not null
                    ? await ResolveSecretValueAsync(
                        config.MixpanelServiceAccountSecretRef,
                        defaultNamespace,
                        cancellationToken
                    )
                    : null,
            };
        }

        private async Task<object> BuildSegmentSinkAsync(
            SegmentSinkConfig? config,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            if (config is null)
                throw new InvalidOperationException(
                    "Segment sink configuration is required for Segment log streams."
                );

            return new
            {
                segmentWriteKey = config.SegmentWriteKeySecretRef is not null
                    ? await ResolveSecretValueAsync(
                        config.SegmentWriteKeySecretRef,
                        defaultNamespace,
                        cancellationToken
                    )
                    : null,
            };
        }

        /// <summary>
        /// Resolves a secret value from a SecretKeySelector reference.
        /// </summary>
        private async Task<string?> ResolveSecretValueAsync(
            SecretKeySelector secretRef,
            string defaultNamespace,
            CancellationToken cancellationToken
        )
        {
            if (
                string.IsNullOrWhiteSpace(secretRef.Name)
                || string.IsNullOrWhiteSpace(secretRef.Key)
            )
                return null;

            var secret = await Kube.GetAsync<V1Secret>(
                secretRef.Name,
                defaultNamespace,
                cancellationToken
            );
            if (secret?.Data is null || !secret.Data.TryGetValue(secretRef.Key, out var value))
            {
                Logger.LogWarning(
                    "Could not resolve secret {SecretName} key {Key}",
                    secretRef.Name,
                    secretRef.Key
                );
                return null;
            }

            return Encoding.UTF8.GetString(value);
        }
    }
}
