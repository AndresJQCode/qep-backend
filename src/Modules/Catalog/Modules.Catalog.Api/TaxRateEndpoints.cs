using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Catalog.Application;

namespace Modules.Catalog.Api;

// Archivo propio y no un bloque más de ProductEndpoints: son dos recursos distintos, y un
// archivo que mapea dos deja de decir su nombre. Comparten el prefijo de ruta, no el archivo.
public static class TaxRateEndpoints
{
    public static IEndpointRouteBuilder MapCatalogTaxRateEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/catalog")
            .WithTags("Catalog");

        group.MapGet("/tax-rates", ListTaxRatesAsync)
            .RequireAuthorization(CatalogPermissions.TaxRateRead)
            .Produces<TaxRatesResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/tax-rates/{taxRateId:guid}", GetTaxRateAsync)
            .RequireAuthorization(CatalogPermissions.TaxRateRead)
            .Produces<TaxRateResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/tax-rates", CreateTaxRateAsync)
            .RequireAuthorization(CatalogPermissions.TaxRateManage)
            .Accepts<CreateTaxRateRequest>("application/json")
            .Produces<TaxRateResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/tax-rates/{taxRateId:guid}", UpdateTaxRateAsync)
            .RequireAuthorization(CatalogPermissions.TaxRateManage)
            .Accepts<UpdateTaxRateRequest>("application/json")
            .Produces<TaxRateResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/tax-rates/{taxRateId:guid}/deactivate", DeactivateTaxRateAsync)
            .RequireAuthorization(CatalogPermissions.TaxRateManage)
            .Produces<TaxRateResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> ListTaxRatesAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var taxRates = await dispatcher.QueryAsync(
            new ListTaxRatesQuery(tenantId),
            cancellationToken);

        return Results.Ok(new TaxRatesResponse(taxRates.Select(ToResponse).ToArray()));
    }

    private static async Task<IResult> GetTaxRateAsync(
        Guid tenantId,
        Guid taxRateId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var taxRate = await dispatcher.QueryAsync(
            new GetTaxRateQuery(tenantId, taxRateId),
            cancellationToken);

        return Results.Ok(ToResponse(taxRate));
    }

    private static async Task<IResult> CreateTaxRateAsync(
        Guid tenantId,
        CreateTaxRateRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var taxRate = await dispatcher.SendAsync(
            new CreateTaxRateCommand(tenantId, request.Name, request.Percentage),
            cancellationToken);

        return Results.Created(
            $"/api/v1/tenants/{tenantId}/catalog/tax-rates/{taxRate.Id}",
            ToResponse(taxRate));
    }

    private static async Task<IResult> UpdateTaxRateAsync(
        Guid tenantId,
        Guid taxRateId,
        UpdateTaxRateRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var taxRate = await dispatcher.SendAsync(
            new UpdateTaxRateCommand(tenantId, taxRateId, request.Name, request.Percentage),
            cancellationToken);

        return Results.Ok(ToResponse(taxRate));
    }

    private static async Task<IResult> DeactivateTaxRateAsync(
        Guid tenantId,
        Guid taxRateId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var taxRate = await dispatcher.SendAsync(
            new DeactivateTaxRateCommand(tenantId, taxRateId),
            cancellationToken);

        return Results.Ok(ToResponse(taxRate));
    }

    private static TaxRateResponse ToResponse(TaxRateDto taxRate) => new(
        taxRate.Id,
        taxRate.Name,
        taxRate.Percentage,
        taxRate.IsActive,
        taxRate.CreatedAt,
        taxRate.UpdatedAt);
}
