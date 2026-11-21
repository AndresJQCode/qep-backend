using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Coordinadora.Application;
using Modules.Coordinadora.Application.Settings;
using Modules.Coordinadora.Application.Shipments;

namespace Modules.Coordinadora.Api;

/// <summary>
/// Las rutas de Coordinadora dentro de su tenant (spec 2026-11-21 §5). Tenant en la ruta: la política frena a quien no tiene el permiso
/// (y, con el módulo apagado, <c>ModuleEntitlementMask</c> ya se lo quitó) y el handler (<c>CoordinadoraAuthorization</c>) a quien lo tiene
/// en otro tenant, con 403 y nunca 404. La configuración vive bajo <c>/api/v1/tenants/{tenantId}/coordinadora/connections/{connectionId}</c>
/// con <c>coordinadora.settings.manage</c> (el <c>PUT</c> lleva <c>If-Match</c>: sin él 428, con una vieja 412); la guía cuelga del pedido
/// con <c>coordinadora.shipment.manage</c>.
/// </summary>
public static class CoordinadoraEndpoints
{
    public static IEndpointRouteBuilder MapCoordinadoraEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var settings = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/coordinadora/connections/{connectionId:guid}")
            .WithTags("Coordinadora")
            .RequireAuthorization(CoordinadoraPermissions.SettingsManage);

        settings.MapGet("/settings", GetSettingsAsync)
            .Produces<CoordinadoraSettingsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        settings.MapPut("/settings", UpdateSettingsAsync)
            .Produces<CoordinadoraSettingsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        // Spec §5.4 y §5.5: la guía de un pedido. Cuelga del pedido y no de la conexión (el tenant tiene una sola), con su propio permiso:
        // quien despacha no tiene por qué configurar.
        var shipment = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/orders/{orderId:guid}/coordinadora-shipment")
            .WithTags("Coordinadora")
            .RequireAuthorization(CoordinadoraPermissions.ShipmentManage);

        shipment.MapGet("/", GetShipmentAsync)
            .Produces<CoordinadoraShipmentDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        shipment.MapGet("/preview", PreviewAsync)
            .Produces<CoordinadoraShipmentPreviewDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        shipment.MapPost("/quote", QuoteAsync)
            .Produces<CoordinadoraQuoteDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        shipment.MapPost("/", CreateAsync)
            .Produces<CoordinadoraShipmentDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // El rótulo: el guardado al crear o, si no, el de Coordinadora en vivo. Mismo permiso que generar.
        shipment.MapGet("/label", GetLabelAsync)
            .Produces(StatusCodes.Status200OK, contentType: "application/pdf")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        shipment.MapPost("/track", TrackAsync)
            .Produces<CoordinadoraShipmentDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        shipment.MapPost("/cancel", CancelAsync)
            .Produces<CoordinadoraShipmentDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        shipment.MapPost("/resolve", ResolveAsync)
            .Produces<CoordinadoraShipmentDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> GetSettingsAsync(
        Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, HttpContext httpContext, CancellationToken cancellationToken) =>
        Respond(httpContext, await dispatcher.QueryAsync(new GetCoordinadoraSettingsQuery(tenantId, connectionId), cancellationToken));

    private static async Task<IResult> UpdateSettingsAsync(
        Guid tenantId,
        Guid connectionId,
        CoordinadoraSettingsRequest request,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        Respond(
            httpContext,
            await dispatcher.SendAsync(
                new UpdateCoordinadoraSettingsCommand(
                    tenantId,
                    connectionId,
                    request.SenderName,
                    request.SenderAddress,
                    request.SenderPhone,
                    request.SenderCityId,
                    request.AccountCode,
                    request.ProductCode,
                    request.ServiceLevel,
                    request.Div,
                    ToInput(request.DefaultPackage),
                    request.PackagingType ?? 0,
                    request.DeclaredValueMinimum ?? 0m,
                    request.CollectOnDelivery ?? false,
                    request.LabelFormat,
                    RequireVersion(httpContext)),
                cancellationToken));

    private static async Task<IResult> GetShipmentAsync(
        Guid tenantId, Guid orderId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetCoordinadoraShipmentQuery(tenantId, orderId), cancellationToken));

    private static async Task<IResult> PreviewAsync(
        Guid tenantId, Guid orderId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new PreviewCoordinadoraShipmentQuery(tenantId, orderId), cancellationToken));

    // El cuerpo es opcional: sin él se usa el paquete por defecto y el total de las líneas como valor declarado.
    private static async Task<IResult> QuoteAsync(
        Guid tenantId, Guid orderId, CoordinadoraShipmentRequest? request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new QuoteCoordinadoraShipmentCommand(tenantId, orderId, ToOverrides(request)), cancellationToken));

    private static async Task<IResult> CreateAsync(
        Guid tenantId, Guid orderId, CoordinadoraShipmentRequest? request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new CreateCoordinadoraShipmentCommand(tenantId, orderId, ToOverrides(request)), cancellationToken));

    private static async Task<IResult> GetLabelAsync(
        Guid tenantId, Guid orderId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var label = await dispatcher.QueryAsync(new GetCoordinadoraLabelQuery(tenantId, orderId), cancellationToken);
        return Results.File(label.Content, "application/pdf", label.FileName);
    }

    private static async Task<IResult> TrackAsync(
        Guid tenantId, Guid orderId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new TrackCoordinadoraShipmentCommand(tenantId, orderId), cancellationToken));

    private static async Task<IResult> CancelAsync(
        Guid tenantId, Guid orderId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new CancelCoordinadoraShipmentCommand(tenantId, orderId), cancellationToken));

    private static async Task<IResult> ResolveAsync(
        Guid tenantId, Guid orderId, CoordinadoraResolveRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new ResolveCoordinadoraShipmentCommand(tenantId, orderId, request.Outcome, request.GuideNumber), cancellationToken));

    private static CoordinadoraPackageInput? ToInput(CoordinadoraPackageRequest? package) =>
        package is null ? null : new CoordinadoraPackageInput(package.HeightCm, package.WidthCm, package.LengthCm, package.WeightKg, package.Units);

    private static CoordinadoraShipmentOverridesInput? ToOverrides(CoordinadoraShipmentRequest? request) =>
        request is null || (request.Package is null && request.DeclaredValue is null)
            ? null
            : new CoordinadoraShipmentOverridesInput(ToInput(request.Package), request.DeclaredValue);

    // El ETag es la versión: lo que la pantalla manda de vuelta en If-Match.
    private static IResult Respond(HttpContext httpContext, CoordinadoraSettingsDto settings)
    {
        httpContext.Response.Headers.ETag = $"\"{settings.Version}\"";
        return Results.Ok(settings);
    }

    // Mismo contrato que el resto del repo: sin If-Match 428, versión vieja 412 (en el handler), y el ETag trae la nueva.
    private static long RequireVersion(HttpContext httpContext) =>
        TryParseVersion(httpContext.Request.Headers.IfMatch, out var version)
            ? version
            : throw new PreconditionRequiredException(
                "precondition.if_match_required",
                "A valid If-Match header containing the loaded settings version is required.");

    // Acepta "7" y el débil W/"7"; la configuración nace en versión 0, así que el 0 es una versión válida.
    internal static bool TryParseVersion(string? etag, out long version)
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
        return long.TryParse(normalized, out version) && version >= 0;
    }
}

/// <summary>El paquete en un cuerpo. Todo nullable: el validador dice qué campo falta, no el enlace del JSON.</summary>
public sealed record CoordinadoraPackageRequest(int? HeightCm, int? WidthCm, int? LengthCm, decimal? WeightKg, int? Units);

/// <summary>El cuerpo de <c>PUT …/settings</c>: la configuración entera. Los interruptores ausentes valen lo de fábrica.</summary>
public sealed record CoordinadoraSettingsRequest(
    string? SenderName,
    string? SenderAddress,
    string? SenderPhone,
    Guid? SenderCityId,
    string? AccountCode,
    string? ProductCode,
    int? ServiceLevel,
    string? Div,
    CoordinadoraPackageRequest? DefaultPackage,
    int? PackagingType,
    decimal? DeclaredValueMinimum,
    bool? CollectOnDelivery,
    string? LabelFormat);

/// <summary>El cuerpo opcional de cotizar y generar: el paquete y el valor declarado de este envío.</summary>
public sealed record CoordinadoraShipmentRequest(CoordinadoraPackageRequest? Package, decimal? DeclaredValue);

/// <summary>El cuerpo de <c>POST …/resolve</c>: <c>exists</c> con el número de guía, o <c>missing</c>.</summary>
public sealed record CoordinadoraResolveRequest(string? Outcome, string? GuideNumber);
