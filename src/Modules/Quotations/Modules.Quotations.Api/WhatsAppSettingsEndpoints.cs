using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Quotations.Application;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Api;

/// <summary>
/// La configuración de WhatsApp del tenant (spec 2026-10-07): un recurso bajo su configuración, con
/// los permisos de settings y no uno nuevo, ETag e If-Match sobre su versión propia. Grupo y tag
/// propios ("Tenant settings"), igual que <see cref="OrdersExportLayoutEndpoints"/>: colgarlo del
/// grupo de cotizaciones lo dejaría con dos tags en OpenAPI. <c>whatsapp-settings</c> no choca con
/// <c>/{quotationId:guid}</c> por la restricción de guid.
/// </summary>
public static class WhatsAppSettingsEndpoints
{
    public static IEndpointRouteBuilder MapWhatsAppSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/quotations/whatsapp-settings")
            .WithTags("Tenant settings");

        group.MapGet("/", GetAsync)
            .RequireAuthorization(TenancyPermissions.SettingsRead)
            .Produces<WhatsAppSettingsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPut("/", UpdateAsync)
            .RequireAuthorization(TenancyPermissions.SettingsUpdate)
            .Accepts<UpdateWhatsAppSettingsRequest>(isOptional: true, "application/json")
            .Produces<WhatsAppSettingsResponse>()
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
        var settings = await dispatcher.QueryAsync(new GetWhatsAppSettingsQuery(tenantId), cancellationToken);
        return SettingsResult(settings, httpContext);
    }

    private static async Task<IResult> UpdateAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken,
        UpdateWhatsAppSettingsRequest? request = null)
    {
        if (!TryParseVersion(httpContext.Request.Headers.IfMatch, out var expectedVersion))
        {
            throw new PreconditionRequiredException(
                "precondition.if_match_required",
                "A valid If-Match header containing the loaded WhatsApp settings version is required.");
        }

        // Mode ausente (o cuerpo ausente) viaja vacío: es el validador el que lo rechaza con errors.Mode.
        // El cuerpo es opcional para el binding: sin eso un PUT vacío sería un 500 de BadHttpRequest.
        var settings = await dispatcher.SendAsync(
            new UpdateWhatsAppSettingsCommand(
                tenantId,
                request?.Mode ?? string.Empty,
                request?.Provider,
                request?.ApiKey,
                request?.FromNumber,
                request?.TemplateId,
                expectedVersion),
            cancellationToken);
        return SettingsResult(settings, httpContext);
    }

    private static IResult SettingsResult(WhatsAppSettingsDto settings, HttpContext httpContext)
    {
        httpContext.Response.Headers.ETag = $"\"{settings.Version}\"";
        return Results.Ok(new WhatsAppSettingsResponse(
            settings.TenantId,
            settings.Mode,
            settings.Provider,
            settings.ApiKeyConfigured,
            settings.ApiKeyUpdatedAt,
            settings.ApiKeyReadable,
            settings.FromNumber,
            settings.TemplateId,
            settings.Modes,
            settings.Providers,
            settings.Version));
    }

    // Copia de OrdersExportLayoutEndpoints.TryParseVersion (privado allá, mismo proyecto): acepta
    // "3", 3 y W/"3".
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

/// <summary>
/// Spec 2026-10-07. <c>apiKey</c> ausente o null conserva la guardada: no hay forma de "borrarla",
/// se cambia de modo. Fuera de <c>Own</c> los campos de la cuenta propia se ignoran.
/// <see cref="ToString"/> esconde la key: el de un record imprime todas sus propiedades.
/// </summary>
public sealed record UpdateWhatsAppSettingsRequest(
    string? Mode,
    string? Provider = null,
    string? ApiKey = null,
    string? FromNumber = null,
    string? TemplateId = null)
{
    public override string ToString() =>
        $"UpdateWhatsAppSettingsRequest {{ Mode = {Mode}, Provider = {Provider}, "
        + $"ApiKey = {(ApiKey is null ? "null" : "***")}, FromNumber = {FromNumber}, TemplateId = {TemplateId} }}";
}

/// <summary>Ver <see cref="WhatsAppSettingsDto"/> para por qué viaja cada campo.</summary>
public sealed record WhatsAppSettingsResponse(
    Guid TenantId,
    string Mode,
    string? Provider,
    bool ApiKeyConfigured,
    DateTimeOffset? ApiKeyUpdatedAt,
    bool? ApiKeyReadable,
    string? FromNumber,
    string? TemplateId,
    IReadOnlyList<string> Modes,
    IReadOnlyList<string> Providers,
    long Version);

/// <summary>Ver <see cref="WhatsAppChannelDto"/>.</summary>
public sealed record WhatsAppChannelResponse(bool Enabled, string Mode);
