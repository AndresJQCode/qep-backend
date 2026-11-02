using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Wms.Application;

namespace Modules.Wms.Api;

/// <summary>Las ubicaciones de una bodega (spec WMS 2026-10-31 §24.3). Cuelga del grupo <c>/wms</c> de <see cref="WmsEndpoints"/>.</summary>
internal static class LocationEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var locations = group.MapGroup("/warehouses/{warehouseId:guid}/locations");

        locations.MapGet("/", ListAsync)
            .RequireAuthorization(WmsPermissions.DocumentRead)
            .Produces<LocationsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        locations.MapPost("/", CreateAsync)
            .RequireAuthorization(WmsPermissions.WarehouseManage)
            .Accepts<LocationRequest>("application/json")
            .Produces<LocationDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Antes de {locationId:guid}: «scannable» no es un guid, pero la ruta fija se lee primero.
        locations.MapGet("/scannable", ScannableAsync)
            .RequireAuthorization(WmsPermissions.DocumentRead)
            .Produces<ScannableLocationsDto>()
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        locations.MapPost("/labels", LabelsAsync)
            .RequireAuthorization(WmsPermissions.WarehouseManage)
            .Accepts<LocationLabelsRequest>("application/json")
            .Produces(StatusCodes.Status200OK, contentType: "application/pdf")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        locations.MapPut("/{locationId:guid}", UpdateAsync)
            .RequireAuthorization(WmsPermissions.WarehouseManage)
            .Accepts<UpdateLocationRequest>("application/json")
            .Produces<LocationDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        locations.MapDelete("/{locationId:guid}", DeleteAsync)
            .RequireAuthorization(WmsPermissions.WarehouseManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> ListAsync(
        Guid tenantId, Guid warehouseId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListLocationsQuery(tenantId, warehouseId), cancellationToken));

    private static async Task<IResult> CreateAsync(
        Guid tenantId, Guid warehouseId, LocationRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var location = await dispatcher.SendAsync(CreateLocationCommand.From(tenantId, warehouseId, request), cancellationToken);

        return Results.Created($"/api/v1/tenants/{tenantId}/wms/warehouses/{warehouseId}/locations/{location.Id}", location);
    }

    private static async Task<IResult> ScannableAsync(
        Guid tenantId, Guid warehouseId, HttpContext http, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var scannable = await dispatcher.QueryAsync(new GetScannableLocationsQuery(tenantId, warehouseId), cancellationToken);
        http.Response.Headers.ETag = scannable.Etag;

        return IfNoneMatchMatches(http.Request.Headers.IfNoneMatch.ToArray(), scannable.Etag)
            ? Results.StatusCode(StatusCodes.Status304NotModified)
            : Results.Ok(scannable);
    }

    /// <summary>Comparación ordinal que acepta el prefijo <c>W/</c>: un proxy puede degradar el etag a débil sin cambiarle el valor.</summary>
    private static bool IfNoneMatchMatches(string?[] headerValues, string etag) =>
        headerValues
            .SelectMany(value => (value ?? string.Empty).Split(','))
            .Select(candidate => candidate.Trim())
            .Select(candidate => candidate.StartsWith("W/", StringComparison.Ordinal) ? candidate[2..] : candidate)
            .Any(candidate => string.Equals(candidate, etag, StringComparison.Ordinal));

    private static async Task<IResult> LabelsAsync(
        Guid tenantId, Guid warehouseId, LocationLabelsRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var pdf = await dispatcher.SendAsync(RenderLocationLabelsCommand.From(tenantId, warehouseId, request), cancellationToken);

        return Results.File(pdf.Content, "application/pdf", pdf.FileName);
    }

    private static async Task<IResult> UpdateAsync(
        Guid tenantId,
        Guid warehouseId,
        Guid locationId,
        UpdateLocationRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(UpdateLocationCommand.From(tenantId, warehouseId, locationId, request), cancellationToken));

    private static async Task<IResult> DeleteAsync(
        Guid tenantId, Guid warehouseId, Guid locationId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new DeleteLocationCommand(tenantId, warehouseId, locationId), cancellationToken);

        return Results.NoContent();
    }
}
