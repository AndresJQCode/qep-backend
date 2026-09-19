using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Api;

public static class TenantSettingsEndpoints
{
    public static IEndpointRouteBuilder MapTenantSettingsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/settings")
            .WithTags("Tenant settings");

        group.MapGet("/", GetAsync)
            .RequireAuthorization(TenancyPermissions.SettingsRead)
            .Produces<TenantSettingsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPatch("/", UpdateAsync)
            .RequireAuthorization(TenancyPermissions.SettingsUpdate)
            .Accepts<UpdateTenantSettingsRequest>("application/json")
            .Produces<TenantSettingsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        // Spec 2026-09-19: el archivo ya sube por el pipeline de Storage (POST /files, PUT
        // prefirmado, complete); este endpoint sólo lo asigna. Mismo permiso y mismo If-Match
        // obligatorio que el PATCH de arriba — administrar el tenant es una sola autoridad.
        group.MapPut("/logo", SetLogoAsync)
            .RequireAuthorization(TenancyPermissions.SettingsUpdate)
            .Accepts<SetTenantLogoRequest>("application/json")
            .Produces<TenantSettingsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapDelete("/logo", RemoveLogoAsync)
            .RequireAuthorization(TenancyPermissions.SettingsUpdate)
            .Produces<TenantSettingsResponse>()
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
        var settings = await dispatcher.QueryAsync(
            new GetTenantSettingsQuery(new TenantId(tenantId)),
            cancellationToken);
        return SettingsResult(settings, httpContext);
    }

    private static async Task<IResult> UpdateAsync(
        Guid tenantId,
        UpdateTenantSettingsRequest request,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var expectedVersion = RequireIfMatch(httpContext);

        var settings = await dispatcher.SendAsync(
            new UpdateTenantSettingsCommand(
                new TenantId(tenantId),
                request.DisplayName,
                request.DefaultCulture,
                request.TimeZone,
                request.DateFormat,
                expectedVersion,
                httpContext.TraceIdentifier),
            cancellationToken);
        return SettingsResult(settings, httpContext);
    }

    private static async Task<IResult> SetLogoAsync(
        Guid tenantId,
        SetTenantLogoRequest request,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var expectedVersion = RequireIfMatch(httpContext);

        var settings = await dispatcher.SendAsync(
            new SetTenantLogoCommand(
                new TenantId(tenantId), request.FileId, expectedVersion, httpContext.TraceIdentifier),
            cancellationToken);
        return SettingsResult(settings, httpContext);
    }

    private static async Task<IResult> RemoveLogoAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var expectedVersion = RequireIfMatch(httpContext);

        var settings = await dispatcher.SendAsync(
            new RemoveTenantLogoCommand(new TenantId(tenantId), expectedVersion, httpContext.TraceIdentifier),
            cancellationToken);
        return SettingsResult(settings, httpContext);
    }

    private static long RequireIfMatch(HttpContext httpContext)
    {
        if (!TryParseVersion(httpContext.Request.Headers.IfMatch, out var expectedVersion))
        {
            throw new PreconditionRequiredException(
                "precondition.if_match_required",
                "A valid If-Match header containing the loaded version is required.");
        }

        return expectedVersion;
    }

    private static IResult SettingsResult(
        TenantSettingsDto settings,
        HttpContext httpContext)
    {
        httpContext.Response.Headers.ETag = $"\"{settings.Version}\"";
        return Results.Ok(new TenantSettingsResponse(
            settings.TenantId.Value,
            settings.DisplayName,
            settings.DefaultCulture,
            settings.TimeZone,
            settings.DateFormat,
            settings.Version,
            settings.Logo is { } logo ? new TenantLogoResponse(logo.FileId, logo.Url) : null));
    }

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

public sealed record UpdateTenantSettingsRequest(
    string DisplayName,
    string DefaultCulture,
    string TimeZone,
    string DateFormat);

public sealed record SetTenantLogoRequest(Guid FileId);

/// <summary>`Url` viaja resuelta (regla BFF del repo): el sidebar necesita un `src` listo, no una
/// clave que armar con una base que el navegador no conoce. Sólo es `null` si el bucket público se
/// desconfiguró después de asignar el logo; `FileId` viaja igual para que la pantalla ofrezca
/// quitar y no subir (spec 2026-09-19, § Contrato).</summary>
public sealed record TenantLogoResponse(Guid FileId, string? Url);

public sealed record TenantSettingsResponse(
    Guid TenantId,
    string DisplayName,
    string DefaultCulture,
    string TimeZone,
    string DateFormat,
    long Version,
    TenantLogoResponse? Logo);
