using System.Threading.Tasks;

using Alethic.Auth0.Operator.Options;
using Alethic.Auth0.Operator.RateLimiting;
using Alethic.Auth0.Operator.Services;

using KubeOps.Operator;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Alethic.Auth0.Operator
{

    public static class Program
    {

        public static Task Main(string[] args)
        {
            // Register custom TypeConverter for Go-style duration parsing (e.g., "10m", "1h30m")
            // Must be called before configuration binding
            GoStyleDurationTypeConverter.Register();

            var builder = Host.CreateApplicationBuilder(args);
            // Note: WatcherHttpTimeout configuration is handled via environment variable KUBEOPS_WATCHER_HTTP_TIMEOUT
            // if needed. Default timeout behavior is managed by KubeOps internally.
            builder.Services.AddKubernetesOperator().RegisterComponents();
            builder.Services.AddMemoryCache();
            builder.Services.Configure<OperatorOptions>(builder.Configuration.GetSection("Auth0:Operator"));

            // Rate limiting services
            builder.Services.Configure<RateLimitOptions>(builder.Configuration.GetSection("Auth0:Operator:RateLimit"));
            builder.Services.AddSingleton<IRateLimiterService, RateLimiterService>();
            builder.Services.AddSingleton<IManagementApiClientFactory, RateLimitAwareApiClientFactory>();

            // Reconciliation scheduling
            builder.Services.AddSingleton<IReconciliationScheduler, ReconciliationScheduler>();
            builder.Services.AddTransient<ITenantRateLimitContextFactory, TenantRateLimitContextFactory>();

            // Polly resilience policies
            builder.Services.AddSingleton<IAuth0ResiliencePolicies, Auth0ResiliencePolicies>();

            // Rx.NET streams for event-driven rate limiting
            builder.Services.AddSingleton<IRateLimitEventStream, RateLimitEventStream>();
            builder.Services.AddSingleton<ITenantReconciliationStreamManager, TenantReconciliationStreamManager>();

            // Client-Connection watcher for cross-resource reconciliation
            builder.Services.AddHostedService<ClientConnectionWatcherService>();

            var app = builder.Build();
            return app.RunAsync();
        }

    }

}
