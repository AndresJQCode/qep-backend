using BuildingBlocks.Application;
using Microsoft.AspNetCore.Http;

namespace Modules.Tenancy.Api;

/// <summary>
/// El If-Match obligatorio de Tenancy: ajustes, logo y estado del tenant. 428 sin él. Acepta el mismo
/// formato que la SPA ya manda (<c>"7"</c>, también débil <c>W/"7"</c>).
/// </summary>
internal static class IfMatchHeader
{
    public static long RequireVersion(HttpContext httpContext)
    {
        if (!TryParseVersion(httpContext.Request.Headers.IfMatch, out var expectedVersion))
        {
            throw new PreconditionRequiredException(
                "precondition.if_match_required",
                "A valid If-Match header containing the loaded version is required.");
        }

        return expectedVersion;
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
