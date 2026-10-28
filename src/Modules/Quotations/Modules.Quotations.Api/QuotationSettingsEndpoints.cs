using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Quotations.Application;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Api;

/// <summary>
/// The tenant's minimum purchase (spec 2026-10-08, D7). Settings permissions and not a new one,
/// the same pair as OrdersExportLayoutEndpoints. The whole document travels both ways: the form
/// repaints what comes back.
/// </summary>
public static class QuotationSettingsEndpoints
{
    public static IEndpointRouteBuilder MapQuotationSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/quotations/settings")
            .WithTags("Tenant settings");

        group.MapGet("/", GetAsync)
            .RequireAuthorization(TenancyPermissions.SettingsRead)
            .Produces<QuotationSettingsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPut("/", UpdateAsync)
            .RequireAuthorization(TenancyPermissions.SettingsUpdate)
            .Accepts<UpdateQuotationSettingsRequest>("application/json")
            .Produces<QuotationSettingsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(ToResponse(await dispatcher.QueryAsync(new GetQuotationSettingsQuery(tenantId), cancellationToken)));

    private static async Task<IResult> UpdateAsync(
        Guid tenantId,
        UpdateQuotationSettingsRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(ToResponse(await dispatcher.SendAsync(
            new UpdateQuotationSettingsCommand(tenantId, request.MinimumUnits, request.MinimumTotals),
            cancellationToken)));

    private static QuotationSettingsResponse ToResponse(QuotationSettingsDto settings) =>
        new(settings.MinimumUnits, settings.MinimumTotals);
}

/// <summary><c>minimumTotals</c> nullable on purpose: a body without it reaches the validator
/// as null and is a 422, instead of silently deleting every total.</summary>
public sealed record UpdateQuotationSettingsRequest(
    int MinimumUnits,
    IReadOnlyDictionary<string, decimal>? MinimumTotals);

public sealed record QuotationSettingsResponse(
    int MinimumUnits,
    IReadOnlyDictionary<string, decimal> MinimumTotals);
