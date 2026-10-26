using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Quotations.Application;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Api;

/// <summary>
/// Los canales de WhatsApp del tenant y el que eligió (spec 2026-10-21 §4.4, §5.2). Leerlos pide gestionar cotizaciones
/// o actualizar la configuración (la pantalla de Configuración los lee con lo segundo); cambiar la elección es sólo de
/// Configuración. Ambos responden el mismo documento, que el formulario repinta.
/// </summary>
public static class WhatsAppChannelEndpoints
{
    public static IEndpointRouteBuilder MapWhatsAppChannelEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/quotations")
            .WithTags("Quotations");

        group.MapGet("/whatsapp-channels", GetAsync)
            .RequireAuthorization(QuotationsPolicies.WhatsAppChannelsRead)
            .Produces<WhatsAppChannelsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPut("/whatsapp-channel", ChooseAsync)
            .RequireAuthorization(TenancyPermissions.SettingsUpdate)
            .Accepts<ChooseWhatsAppChannelRequest>("application/json")
            .Produces<WhatsAppChannelsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetWhatsAppChannelsQuery(tenantId), cancellationToken));

    private static async Task<IResult> ChooseAsync(
        Guid tenantId,
        ChooseWhatsAppChannelRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new ChooseWhatsAppChannelCommand(tenantId, request.ConnectionId), cancellationToken));
}

/// <summary><c>ConnectionId</c> nullable a propósito: un cuerpo sin él llega al validador como null y es un 422 con el
/// campo, no un 400 del binder.</summary>
public sealed record ChooseWhatsAppChannelRequest(Guid? ConnectionId);
