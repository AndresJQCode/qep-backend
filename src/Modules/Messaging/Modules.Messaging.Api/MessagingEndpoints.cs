using Microsoft.AspNetCore.Routing;

namespace Modules.Messaging.Api;

public static class MessagingEndpoints
{
    /// <summary>Se llena desde la Task 14: lista, detalle, hilo, envío, leído, resolver, reabrir,
    /// búsqueda y medio, todos bajo <c>/api/v1/tenants/{tenantId:guid}/messaging</c>.</summary>
    public static IEndpointRouteBuilder MapMessagingEndpoints(this IEndpointRouteBuilder endpoints) => endpoints;
}
