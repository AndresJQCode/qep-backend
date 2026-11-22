using BuildingBlocks.Application.Realtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Bootstrapper.Realtime;

public static class AppRealtimeExtensions
{
    /// <summary>Plan N1: under <c>/api/v1/hubs</c> because <c>TenantRequestResolver</c> takes the <c>tenantId</c> query only there (the
    /// browser's WebSocket carries no headers), and because the Vite proxy and the Vercel rewrite only forward <c>/api/*</c>.</summary>
    public const string HubPath = "/api/v1/hubs/app";

    /// <summary>Plan N24: called from <c>AddQepPlatform</c>, so <c>MultiReplicaInventoryTests</c> sees it without one more replayed
    /// <c>Program.cs</c> registration. <c>AddSignalR</c> is idempotent with Messaging's.</summary>
    public static IServiceCollection AddAppRealtime(this IServiceCollection services)
    {
        services.AddSignalR();
        services.AddSingleton<AppHubConnections>();
        services.AddSingleton<AppHubDispatcher>();
        // One instance behind both ports: the member publisher is the same pg_notify with the member in the envelope.
        services.AddSingleton<PgRealtimePublisher>();
        services.AddSingleton<IRealtimePublisher>(provider => provider.GetRequiredService<PgRealtimePublisher>());
        services.AddSingleton<IRealtimeMemberPublisher>(provider => provider.GetRequiredService<PgRealtimePublisher>());
        services.AddHostedService<RealtimeEventsListener>();
        // Spec 2026-11-22 D-9: the inbound-message alert is delivered by each pod to its own connections, next to Messaging's own sink.
        services.AddSingleton<Modules.Messaging.Application.IMessagingEventSink, AppHubMessagingAlertSink>();
        // Presence (spec §5.3): the registry itself is registered by AddQepValkey with the Presence area (local or Valkey); the names
        // follow the rule MessagingMemberNames owns (plan N12).
        services.AddScoped<IRealtimeMemberNames, MessagingMemberNames>();
        services.AddHostedService<RealtimePresenceSweeper>();
        return services;
    }

    /// <summary>Session only, no module permission: the hub belongs to no module, and each topic is authorized on <c>Subscribe</c>.</summary>
    public static IEndpointRouteBuilder MapAppHub(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<AppHub>(HubPath).RequireAuthorization();
        return endpoints;
    }
}
