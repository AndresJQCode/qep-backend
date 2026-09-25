using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Quotations.Application;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Api;

/// <summary>
/// La homologación de columnas del Excel de pedidos (spec 2026-09-24): un recurso por tenant bajo
/// su configuración, con los permisos de settings y no uno nuevo (D5), ETag e If-Match sobre la
/// versión propia del layout (D6, D9) y la lista entera en cada respuesta (D7). Sin DELETE (D10):
/// restaurar es un PUT con el catálogo en su orden y nombres, sin fijas, que el GET ya trae en
/// <c>defaultHeader</c> y <c>defaultPosition</c>.
/// </summary>
public static class OrdersExportLayoutEndpoints
{
    public static IEndpointRouteBuilder MapOrdersExportLayoutEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/orders-export-layout")
            .WithTags("Tenant settings");

        group.MapGet("/", GetAsync)
            .RequireAuthorization(TenancyPermissions.SettingsRead)
            .Produces<OrdersExportLayoutResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPut("/", UpdateAsync)
            .RequireAuthorization(TenancyPermissions.SettingsUpdate)
            .Accepts<UpdateOrdersExportLayoutRequest>("application/json")
            .Produces<OrdersExportLayoutResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var layout = await dispatcher.QueryAsync(
            new GetOrdersExportLayoutQuery(tenantId), cancellationToken);
        return LayoutResult(layout, httpContext);
    }

    private static async Task<IResult> UpdateAsync(
        Guid tenantId,
        UpdateOrdersExportLayoutRequest request,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!TryParseVersion(httpContext.Request.Headers.IfMatch, out var expectedVersion))
        {
            throw new PreconditionRequiredException(
                "precondition.if_match_required",
                "A valid If-Match header containing the loaded layout version is required.");
        }

        // Columns nula viaja nula: es el validador el que la rechaza con errors.Columns.
        var layout = await dispatcher.SendAsync(
            new UpdateOrdersExportLayoutCommand(
                tenantId,
                request.Columns?
                    .Select(column => new OrdersExportColumnInput(
                        column.Kind, column.Key, column.Header, column.Value, column.Visible))
                    .ToArray(),
                expectedVersion,
                httpContext.TraceIdentifier),
            cancellationToken);
        return LayoutResult(layout, httpContext);
    }

    private static IResult LayoutResult(OrdersExportLayoutDto layout, HttpContext httpContext)
    {
        httpContext.Response.Headers.ETag = $"\"{layout.Version}\"";
        return Results.Ok(new OrdersExportLayoutResponse(
            layout.TenantId,
            layout.Columns
                .Select(column => new OrdersExportColumnResponse(
                    column.Kind,
                    column.Key,
                    column.DefaultHeader,
                    column.DefaultPosition,
                    column.Header,
                    column.Value,
                    column.Visible))
                .ToArray(),
            layout.Version));
    }

    // Copia de OrderEndpoints.TryParseVersion (mismo proyecto, privado allá): acepta "3", 3 y
    // W/"3" (Review Focus 2).
    private static bool TryParseVersion(string? etag, out long version)
    {
        version = 0;
        if (string.IsNullOrWhiteSpace(etag))
        {
            return false;
        }

        var normalized = etag.Trim();
        if (normalized.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..].Trim();
        }

        normalized = normalized.Trim('"');
        return long.TryParse(normalized, out version) && version > 0;
    }
}

/// <summary>`columns` nullable a propósito: `{}` llega como nula y el validador la marca, en vez
/// de convertirse en "restaurar todo" por accidente.</summary>
public sealed record UpdateOrdersExportLayoutRequest(IReadOnlyList<OrdersExportColumnRequest>? Columns);

public sealed record OrdersExportColumnRequest(
    string? Kind,
    string? Key,
    string? Header,
    string? Value,
    bool Visible);

public sealed record OrdersExportLayoutResponse(
    Guid TenantId,
    IReadOnlyList<OrdersExportColumnResponse> Columns,
    long Version);

/// <summary>`DefaultHeader` y `DefaultPosition` (1-based) viajan por columna (regla BFF): la
/// pantalla los necesita como placeholder, para "restaurar" una sola y para "restaurar todo" sin
/// conocer el catálogo. Nulos en una fija.</summary>
public sealed record OrdersExportColumnResponse(
    string Kind,
    string? Key,
    string? DefaultHeader,
    int? DefaultPosition,
    string Header,
    string? Value,
    bool Visible);
