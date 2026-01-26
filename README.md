# Kubernetes operator for Auth0 management

## About The Project

This Auth0 Kubernetes Operator is responsible for managing the lifecycle of Auth0 resources in a Kubernetes cluster.

It automates the deployment, configuration, and management of Auth0 resources, such as clients, connections, resource servers and more.

## Architecture

### Rate Limiting

The operator implements a sophisticated rate limiting system to respect Auth0's API quotas and prevent 429 errors:

```
┌─────────────────────────────────────────────────────────────────────────┐
│                        V1TenantEntityController                          │
│  ┌────────────────────────────────────────────────────────────────────┐ │
│  │ Reconcile() → IReconciliationScheduler.BeginReconcileAsync()       │ │
│  │   • Startup spread (prevent thundering herd on restart)            │ │
│  │   • Proactive throttling (defer when approaching limits)           │ │
│  │   • Circuit breaker (defer when circuit is open)                   │ │
│  └────────────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                      Rate Limiting Components                            │
├─────────────────┬─────────────────┬─────────────────┬───────────────────┤
│ Reconciliation  │ Polly Resilience│ Rx.NET Streams  │ Rate Limiter      │
│ Scheduler       │ Policies        │                 │ Service           │
├─────────────────┼─────────────────┼─────────────────┼───────────────────┤
│ • Startup spread│ • Rate limit    │ • Burst detect  │ • State tracking  │
│ • Circuit check │   retry (3x)    │ • Circuit open  │ • Header parsing  │
│ • Throttle check│ • Transient     │   recommendations│ • Proactive      │
│                 │   retry (3x)    │                 │   throttling      │
│                 │ • Circuit break │                 │                   │
└─────────────────┴─────────────────┴─────────────────┴───────────────────┘
```

**Key Features:**
- **Startup Spread**: On operator restart, reconciliations are spread over a configurable window (default 30s) using stable hashing to prevent all resources hitting the API simultaneously
- **Proactive Throttling**: When rate limit headers indicate low remaining quota, reconciliations are deferred before hitting 429 errors
- **Polly Resilience**: HTTP-level retry with exponential backoff for transient errors and rate limits
- **Rx.NET Pattern Detection**: Detects burst patterns and recommends circuit opening when repeated rate limits are hit
- **Per-Tenant Isolation**: Each Auth0 tenant has independent rate limit tracking and circuit state

All rate limiting features are enabled by default and configured via `RateLimitOptions` in the operator configuration.

### Installation

`helm install -n auth0 auth0 oci://ghcr.io/alethic/auth0-operator`

## Usage

This operator is a cluster-wide operator. We would like to eventually support namespace-only (TODO).

Each available Auth0 resource type exposed by the management type is mapped nearly 1:1 to a Kubernetes document. Tenant is Tenant, Client is Client, etc. Resources each have a `spec.conf` entry which represents the contents of an Auth0 Management API update or create request to apply. Resources also each have a `spec.init` entry which represents the same schema as `spec.conf`, but is only used on initial resource creation. Additionally, some resources have a `spec.find` entry which determines how the operator locates an existing Auth0 entity.

A secret is required to authenticate with Auth0's management API. This secret must contain the `clientId` and `clientSecret` fields.

At least a single `Tenant` resource is required. This `Tenant` resource must contain `spec.auth` with `domain` and `secretRef` to specify the authentication information.

Other resources, such as `Client`, `ResourceServer`, etc, must have a `spec.tenantRef` value refering to the owning tenant to manage. The name of the Kubernetes resource does not refer to the `name` field in the Auth0 Management API.

Each resource has a `spec.policy` entry which is a list of the following possible values: `Create`, `Update`, `Delete`. These policies determine what permissions the Auth0 operator has: Can it create new entities? Can it update existing entities? Can it delete remote entities?

Since the entire API is derived from the Auth0 Management API their documentation is relevant: [Auth0 Management API](https://auth0.com/docs/api/management/v2).

## Supported Resources

- [x] kubernetes.auth0.com/v1:Tenant `a0tenant`
- [x] kubernetes.auth0.com/v1:Client `a0app`
- [x] kubernetes.auth0.com/v1:ClientGrant `a0cgr`
- [x] kubernetes.auth0.com/v1:ResourceServer `a0api`
- [x] kubernetes.auth0.com/v1:Connection `a0con`
- [x] kubernetes.auth0.com/v1:Auth0Role `a0role`
- [x] kubernetes.auth0.com/v1:RolePermission `a0rp`
- [x] kubernetes.auth0.com/v1:LogStream `a0ls`
- [x] kubernetes.auth0.com/v1:EventStream `a0es` *(Beta - Auth0 Early Access API)*

## Examples

### Tenant

```
apiVersion: kubernetes.auth0.com/v1
kind: Tenant
metadata:
  name: example-tenant
  namespace: example
spec:
  auth:
    domain: example-tenant.us.auth0.com
    secretRef:
      name: example-tenant
  conf:
    friendly_name: My Tenant
  name: example-tenant
```

### Client (App)

https://auth0.com/docs/get-started/applications

```
apiVersion: kubernetes.auth0.com/v1
kind: Client
metadata:
  name: example-client
  namespace: example
spec:
  tenantRef:
    name: example-tenant
  secretRef:
    name: example-client-secret
  conf:
    name: example-client
    app_type: spa
    grant_types:
      - client_credentials
```

#### Enable connections from the client

Clients can declaratively enable or disable Auth0 connections via `spec.conf.enabled_connections`. The operator will reconcile the list by enabling new connections and disabling any that are removed.

```
apiVersion: kubernetes.auth0.com/v1
kind: Client
metadata:
  name: example-client
  namespace: example
spec:
  tenantRef:
    name: example-tenant
  conf:
    name: example-client
    app_type: spa
    enabled_connections:
      - name: google-workspace
        namespace: shared-connections
      - name: username-password
```

## Client Secret

The Client resource supports an optional `secretRef` field which can point to either an existing secret (not implemented) or the name of a secret to be created with the extraction of the `client_id` and `client_secret` values from the app.

### JSON Format Output

For integration with systems like AWS Secrets Manager (via ACK controller) that require credentials in a single JSON key, you can configure the secret output format:

```yaml
apiVersion: kubernetes.auth0.com/v1
kind: Client
metadata:
  name: my-client
spec:
  tenantRef:
    name: my-tenant
  secretRef:
    name: my-client-secret
    format: json           # Enables JSON output
    jsonKey: credentials   # Optional: key name for JSON (defaults to "credentials")
  conf:
    name: my-client
    app_type: non_interactive
```

This produces a secret with:
- `clientId`: The client ID (separate key for backward compatibility)
- `clientSecret`: The client secret (separate key for backward compatibility)
- `credentials`: JSON containing `{"clientId":"...","clientSecret":"..."}` (when format is "json")

## ResourceServer

https://auth0.com/docs/get-started/apis

```
apiVersion: kubernetes.auth0.com/v1
kind: ResourceServer
metadata:
  name: example-api
  namespace: example
spec:
  tenantRef:
    name: example-tenant
  conf:
    identifier: https://example.com/
    name: Example API
    allow_offline_access: false
    skip_consent_for_verifiable_first_party_clients: true
    token_lifetime: 86400
    token_lifetime_for_web: 7200
    signing_alg: RS256
    token_dialect: access_token
```

### ClientGrant

Grants permission for a Client to access a ResourceServer.

```
apiVersion: kubernetes.auth0.com/v1
kind: ClientGrant
metadata:
  name: example-app-api
  namespace: example
spec:
  tenantRef:
    name: example-tenant
  conf:
    clientRef:
      name: example-client
    audience:
      name: example-api
    scope: []
```

### Auth0Role

Manages Auth0 roles for RBAC. Roles are containers for permissions that can be assigned to users.

https://auth0.com/docs/manage-users/access-control/rbac

```yaml
apiVersion: kubernetes.auth0.com/v1
kind: Auth0Role
metadata:
  name: admin-role
  namespace: example
spec:
  tenantRef:
    name: example-tenant
  policy:
    - Create
    - Update
    - Delete
  find:
    nameFilter: Admin  # Optional: find existing role by name
  conf:
    name: Admin
    description: Administrator role with full access
```

### RolePermission

Assigns permissions from a ResourceServer (API) to a Role. This solves the CloudFormation 4096-byte response limit by managing permissions separately from roles.

> **Note:** RolePermission handles large permission sets (60+) that exceed CloudFormation limits by managing them independently.

```yaml
apiVersion: kubernetes.auth0.com/v1
kind: RolePermission
metadata:
  name: admin-api-permissions
  namespace: example
spec:
  tenantRef:
    name: example-tenant
  roleRef:
    name: admin-role
  resourceServerRef:
    name: example-api
    # Or use identifier directly:
    # identifier: https://example.com/api
  policy:
    - Create
    - Update
    - Delete
  conf:
    permissions:
      - read:users
      - write:users
      - delete:users
      - read:settings
      - write:settings
```

### LogStream

Configures log streaming to external destinations. Supports 8 destination types.

https://auth0.com/docs/customize/log-streams

**Supported Types:** `http`, `eventbridge`, `eventgrid`, `datadog`, `splunk`, `sumo`, `mixpanel`, `segment`

#### HTTP Webhook Example

```yaml
apiVersion: kubernetes.auth0.com/v1
kind: LogStream
metadata:
  name: logs-to-webhook
  namespace: example
spec:
  tenantRef:
    name: example-tenant
  policy:
    - Create
    - Update
    - Delete
  conf:
    name: Webhook Log Stream
    type: http
    status: active
    filters:
      - type: category
        name: auth.login.success
    sink:
      http:
        httpEndpoint: https://logs.example.com/auth0
        httpContentType: application/json
        httpContentFormat: JSONLINES
        httpAuthorizationSecretRef:
          name: logstream-auth-secret
          key: authorization
```

#### AWS EventBridge Example

```yaml
apiVersion: kubernetes.auth0.com/v1
kind: LogStream
metadata:
  name: logs-to-eventbridge
  namespace: example
spec:
  tenantRef:
    name: example-tenant
  conf:
    name: EventBridge Log Stream
    type: eventbridge
    status: active
    sink:
      eventBridge:
        awsAccountId: "123456789012"
        awsRegion: us-east-1
```

#### Datadog Example

```yaml
apiVersion: kubernetes.auth0.com/v1
kind: LogStream
metadata:
  name: logs-to-datadog
  namespace: example
spec:
  tenantRef:
    name: example-tenant
  conf:
    name: Datadog Log Stream
    type: datadog
    status: active
    sink:
      datadog:
        datadogRegion: us
        datadogApiKeySecretRef:
          name: datadog-secret
          key: api-key
```

### EventStream (Beta)

Subscribes to Auth0 lifecycle events via CloudEvents. This is an **Early Access API** from Auth0.

https://auth0.com/docs/customize/integrations/event-streams

> **Warning:** The Auth0 EventStreams API is in Early Access and may change. Use with caution in production.

**Supported Event Types:**
- User: `user.created`, `user.updated`, `user.deleted`
- Organization: `organization.created`, `organization.updated`, `organization.deleted`
- Organization Members: `organization.member.added`, `organization.member.deleted`, `organization.member.role.assigned`, `organization.member.role.deleted`
- Organization Connections: `organization.connection.added`, `organization.connection.updated`, `organization.connection.removed`

#### Webhook Example

```yaml
apiVersion: kubernetes.auth0.com/v1
kind: EventStream
metadata:
  name: user-events-webhook
  namespace: example
spec:
  tenantRef:
    name: example-tenant
  policy:
    - Create
    - Update
    - Delete
  conf:
    name: User Events Stream
    type: webhook
    status: active
    subscriptions:
      - eventType: user.created
      - eventType: user.updated
      - eventType: user.deleted
    sink:
      webhook:
        url: https://events.example.com/auth0
        authorizationSecretRef:
          name: eventstream-auth-secret
          key: bearer-token
```

#### AWS EventBridge Example

```yaml
apiVersion: kubernetes.auth0.com/v1
kind: EventStream
metadata:
  name: org-events-eventbridge
  namespace: example
spec:
  tenantRef:
    name: example-tenant
  conf:
    name: Organization Events Stream
    type: eventbridge
    status: active
    subscriptions:
      - eventType: organization.created
      - eventType: organization.member.added
      - eventType: organization.member.deleted
    sink:
      eventBridge:
        awsAccountId: "123456789012"
        awsRegion: us-east-1
```
