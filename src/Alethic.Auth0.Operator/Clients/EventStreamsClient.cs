using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Alethic.Auth0.Operator.Clients
{

    /// <summary>
    /// Client for the Auth0 Event Streams API (Early Access).
    /// This provides direct HTTP access since the API is not yet in the Auth0 SDK.
    /// 
    /// API Endpoints:
    /// - GET    /api/v2/event-streams       - List all event streams
    /// - GET    /api/v2/event-streams/{id}  - Get event stream by ID
    /// - POST   /api/v2/event-streams       - Create event stream
    /// - PATCH  /api/v2/event-streams/{id}  - Update event stream
    /// - DELETE /api/v2/event-streams/{id}  - Delete event stream
    /// </summary>
    public class EventStreamsClient : IDisposable
    {

        private readonly HttpClient _httpClient;
        private readonly Uri _baseUri;
        private readonly string _token;
        private readonly bool _ownsHttpClient;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Initializes a new instance of the EventStreamsClient.
        /// </summary>
        /// <param name="token">Auth0 Management API access token</param>
        /// <param name="baseUri">Base URI for the Auth0 tenant (e.g., https://tenant.auth0.com/api/v2/)</param>
        /// <param name="httpClient">Optional HttpClient to use</param>
        public EventStreamsClient(string token, Uri baseUri, HttpClient? httpClient = null)
        {
            _token = token ?? throw new ArgumentNullException(nameof(token));
            _baseUri = baseUri ?? throw new ArgumentNullException(nameof(baseUri));
            _ownsHttpClient = httpClient == null;
            _httpClient = httpClient ?? new HttpClient();
        }

        /// <summary>
        /// Gets all event streams for the tenant.
        /// </summary>
        public async Task<IList<EventStreamResponse>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            var uri = new Uri(_baseUri, "event-streams");
            using var request = CreateRequest(HttpMethod.Get, uri);
            
            var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<EventStreamResponse>>(content, JsonOptions) ?? new List<EventStreamResponse>();
        }

        /// <summary>
        /// Gets an event stream by ID.
        /// </summary>
        public async Task<EventStreamResponse?> GetAsync(string id, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(id));

            var uri = new Uri(_baseUri, $"event-streams/{Uri.EscapeDataString(id)}");
            using var request = CreateRequest(HttpMethod.Get, uri);
            
            var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<EventStreamResponse>(content, JsonOptions);
        }

        /// <summary>
        /// Creates a new event stream.
        /// </summary>
        public async Task<EventStreamResponse> CreateAsync(EventStreamCreateRequest createRequest, CancellationToken cancellationToken = default)
        {
            var uri = new Uri(_baseUri, "event-streams");
            using var request = CreateRequest(HttpMethod.Post, uri, createRequest);
            
            var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<EventStreamResponse>(content, JsonOptions) 
                ?? throw new InvalidOperationException("Failed to deserialize event stream response");
        }

        /// <summary>
        /// Updates an existing event stream.
        /// </summary>
        public async Task<EventStreamResponse> UpdateAsync(string id, EventStreamUpdateRequest updateRequest, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(id));

            var uri = new Uri(_baseUri, $"event-streams/{Uri.EscapeDataString(id)}");
            
            // PATCH method
            using var request = CreateRequest(new HttpMethod("PATCH"), uri, updateRequest);
            
            var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<EventStreamResponse>(content, JsonOptions) 
                ?? throw new InvalidOperationException("Failed to deserialize event stream response");
        }

        /// <summary>
        /// Deletes an event stream.
        /// </summary>
        public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(id));

            var uri = new Uri(_baseUri, $"event-streams/{Uri.EscapeDataString(id)}");
            using var request = CreateRequest(HttpMethod.Delete, uri);
            
            var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, object? body = null)
        {
            var request = new HttpRequestMessage(method, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (body != null)
            {
                var json = JsonSerializer.Serialize(body, JsonOptions);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            return request;
        }

        private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new EventStreamsApiException(response.StatusCode, content);
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
        }

    }

    /// <summary>
    /// Exception thrown when the Event Streams API returns an error.
    /// </summary>
    public class EventStreamsApiException : Exception
    {
        public HttpStatusCode StatusCode { get; }
        public string ResponseBody { get; }

        public EventStreamsApiException(HttpStatusCode statusCode, string responseBody)
            : base($"Event Streams API error: {statusCode} - {responseBody}")
        {
            StatusCode = statusCode;
            ResponseBody = responseBody;
        }
    }

    /// <summary>
    /// Request model for creating an event stream.
    /// </summary>
    public class EventStreamCreateRequest
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("subscriptions")]
        public List<EventStreamSubscriptionRequest>? Subscriptions { get; set; }

        [JsonPropertyName("destination")]
        public EventStreamDestination? Destination { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }
    }

    /// <summary>
    /// Request model for updating an event stream.
    /// </summary>
    public class EventStreamUpdateRequest
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("subscriptions")]
        public List<EventStreamSubscriptionRequest>? Subscriptions { get; set; }

        [JsonPropertyName("destination")]
        public EventStreamDestination? Destination { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }
    }

    /// <summary>
    /// Response model for event stream operations.
    /// </summary>
    public class EventStreamResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("subscriptions")]
        public List<EventStreamSubscriptionResponse>? Subscriptions { get; set; }

        [JsonPropertyName("destination")]
        public EventStreamDestination? Destination { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("created_at")]
        public DateTime? CreatedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTime? UpdatedAt { get; set; }
    }

    /// <summary>
    /// Event subscription in request.
    /// </summary>
    public class EventStreamSubscriptionRequest
    {
        [JsonPropertyName("event_type")]
        public string? EventType { get; set; }
    }

    /// <summary>
    /// Event subscription in response.
    /// </summary>
    public class EventStreamSubscriptionResponse
    {
        [JsonPropertyName("event_type")]
        public string? EventType { get; set; }
    }

    /// <summary>
    /// Destination configuration for event streams.
    /// Supports webhook, eventbridge, and action destinations.
    /// </summary>
    public class EventStreamDestination
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("configuration")]
        public EventStreamDestinationConfiguration? Configuration { get; set; }
    }

    /// <summary>
    /// Configuration for event stream destinations.
    /// </summary>
    public class EventStreamDestinationConfiguration
    {
        // Webhook configuration
        [JsonPropertyName("webhook_endpoint")]
        public string? WebhookEndpoint { get; set; }

        [JsonPropertyName("webhook_authorization")]
        public List<EventStreamWebhookAuthorization>? WebhookAuthorization { get; set; }

        // EventBridge configuration
        [JsonPropertyName("aws_account_id")]
        public string? AwsAccountId { get; set; }

        [JsonPropertyName("aws_region")]
        public string? AwsRegion { get; set; }

        [JsonPropertyName("aws_partner_event_source")]
        public string? AwsPartnerEventSource { get; set; }

        // Action configuration
        [JsonPropertyName("action_id")]
        public string? ActionId { get; set; }
    }

    /// <summary>
    /// Webhook authorization configuration.
    /// </summary>
    public class EventStreamWebhookAuthorization
    {
        [JsonPropertyName("method")]
        public string? Method { get; set; }

        // For basic auth
        [JsonPropertyName("username")]
        public string? Username { get; set; }

        [JsonPropertyName("password")]
        public string? Password { get; set; }

        // For bearer token
        [JsonPropertyName("token")]
        public string? Token { get; set; }
    }

}
