using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Quotations.Application;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Api;

/// <summary>
/// Los ajustes del pedido por tenant (spec 2026-11-10 §6), hermanos de <c>/orders-export-layout</c>: los
/// permisos de settings y no uno nuevo, y el documento entero en los dos sentidos, que es lo que el
/// formulario repinta.
/// </summary>
public static class OrderSettingsEndpoints
{
    public static IEndpointRouteBuilder MapOrderSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/orders-settings")
            .WithTags("Tenant settings");

        group.MapGet("/", GetAsync)
            .RequireAuthorization(TenancyPermissions.SettingsRead)
            .Produces<OrderSettingsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPut("/", UpdateAsync)
            .RequireAuthorization(TenancyPermissions.SettingsUpdate)
            .Accepts<UpdateOrderSettingsRequest>("application/json")
            .Produces<OrderSettingsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(ToResponse(await dispatcher.QueryAsync(new GetOrderSettingsQuery(tenantId), cancellationToken)));

    private static async Task<IResult> UpdateAsync(
        Guid tenantId,
        UpdateOrderSettingsRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(ToResponse(await dispatcher.SendAsync(
            new UpdateOrderSettingsCommand(
                tenantId,
                request.RequirePaymentProofToMarkPaid,
                request.RequireFullPaymentToInvoice,
                request.AutoArchive),
            cancellationToken)));

    private static OrderSettingsResponse ToResponse(OrderSettingsDto settings) =>
        new(settings.RequirePaymentProofToMarkPaid, settings.RequireFullPaymentToInvoice, settings.AutoArchive);
}

/// <summary>Los tres nullable: un campo ausente es un 422 con su clave en <c>errors</c>, no un
/// <c>false</c> silencioso.</summary>
public sealed record UpdateOrderSettingsRequest(
    bool? RequirePaymentProofToMarkPaid,
    bool? RequireFullPaymentToInvoice,
    bool? AutoArchive);

public sealed record OrderSettingsResponse(
    bool RequirePaymentProofToMarkPaid,
    bool RequireFullPaymentToInvoice,
    bool AutoArchive);
