using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using BuildingBlocks.Application;
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
        if (!TryParseVersion(httpContext.Request.Headers.IfMatch, out var expectedVersion))
        {
            throw new PreconditionRequiredException(
                "precondition.if_match_required",
                "A valid If-Match header containing the loaded version is required.");
        }

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
            settings.Version));
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

public sealed record TenantSettingsResponse(
    Guid TenantId,
    string DisplayName,
    string DefaultCulture,
    string TimeZone,
    string DateFormat,
    long Version);
