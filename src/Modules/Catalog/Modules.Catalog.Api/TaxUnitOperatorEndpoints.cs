using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Catalog.Application;
using Modules.Tenancy.Domain;

namespace Modules.Catalog.Api;

/// <summary>
/// La consola «Unidad tributaria» del operador. La ruta lleva el tenant desde el que actúa el operador, como la consola de
/// ciudades. Doble capa: la política exige <c>operator.tax_units.*</c> y el handler revalida con <c>OperatorAuthorization</c>.
/// </summary>
public static class TaxUnitOperatorEndpoints
{
    public static IEndpointRouteBuilder MapTaxUnitOperatorEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/operator/tax-units")
            .WithTags("Operator");

        group.MapGet("/", ListAsync)
            .RequireAuthorization(CatalogOperatorPermissions.TaxUnitsRead)
            .Produces<TaxUnitsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPut("/{country}/{year:int}", SetAsync)
            .RequireAuthorization(CatalogOperatorPermissions.TaxUnitsManage)
            .Accepts<SetTaxUnitValueRequest>("application/json")
            .Produces<TaxUnitValueDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListTaxUnitsQuery(new TenantId(tenantId)), cancellationToken));

    // Un value ausente llega como 0 y lo rechaza el dominio (value_invalid), no el binder.
    private static async Task<IResult> SetAsync(
        Guid tenantId,
        string country,
        int year,
        SetTaxUnitValueRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new SetTaxUnitValueCommand(new TenantId(tenantId), country, year, request.Value ?? 0m),
            cancellationToken));
}

public sealed record SetTaxUnitValueRequest(decimal? Value);
