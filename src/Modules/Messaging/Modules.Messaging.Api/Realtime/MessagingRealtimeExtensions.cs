using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;

namespace Modules.Messaging.Api.Realtime;

public static class MessagingRealtimeExtensions
{
    /// <summary>Spec 2026-10-14, contrato §2.1. El tenant viaja también en el query <c>tenantId</c> (el handshake WebSocket
    /// no lleva headers); <c>TenantRequestResolver</c> lo acepta sólo bajo este prefijo.</summary>
    public const string HubPath = "/api/v1/hubs/messaging";

    /// <summary>SignalR y el sink que lleva al hub lo que el listener de Infrastructure recibe por <c>NOTIFY</c>. Se
    /// registra además del de Infrastructure, no en su lugar: el listener reparte a todos los sinks.</summary>
    public static IServiceCollection AddMessagingRealtime(this IServiceCollection services)
    {
        services.AddSignalR();
        services.AddSingleton<IMessagingEventSink, MessagingHubEventSink>();
        return services;
    }

    /// <summary>La política del endpoint exige <c>messaging.conversation.read</c>; el hub revalida módulo y membresía al
    /// conectar. El <c>negotiate</c> es un <c>POST</c> y pasa por la defensa CSRF como cualquier otro: el cliente manda
    /// <c>X-Qep-Client: web</c>.</summary>
    public static IEndpointRouteBuilder MapMessagingHub(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<MessagingHub>(HubPath).RequireAuthorization(MessagingPermissions.ConversationRead);
        return endpoints;
    }
}
