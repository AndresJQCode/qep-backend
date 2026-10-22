using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modules.Automations.Api;

public static class AutomationsEndpoints
{
    /// <summary>Spec 2026-10-21 §4.4: todo bajo <c>/api/v1/tenants/{tenantId:guid}/automations</c>. Cada ruta declara su
    /// <c>RequireAuthorization</c>; los handlers revalidan tenant, permiso y capacidad (403 con código, nunca 404 ante otro tenant).
    /// </summary>
    public static IEndpointRouteBuilder MapAutomationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/tenants/{tenantId:guid}/automations").WithTags("Automations");
        group.MapChatbotEndpoints();
        return endpoints;
    }

    // Copia de MessagingEndpoints.RequireVersion: sin If-Match 428, vieja 412 (en el handler, AutomationsVersions).
    internal static long RequireVersion(HttpContext httpContext) =>
        TryParseVersion(httpContext.Request.Headers.IfMatch, out var version)
            ? version
            : throw new PreconditionRequiredException(
                "precondition.if_match_required",
                "A valid If-Match header containing the loaded chatbot version is required.");

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
