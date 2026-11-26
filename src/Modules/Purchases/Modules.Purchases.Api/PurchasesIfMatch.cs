using BuildingBlocks.Application;
using Microsoft.AspNetCore.Http;

namespace Modules.Purchases.Api;

/// <summary>
/// La versión de <c>If-Match</c>; sin ella, 428 <c>precondition.if_match_required</c>; la versión vieja es 412 en el handler. Copia
/// literal de <c>Modules.Catalog.Api/CatalogIfMatch.cs</c> (y de <c>Modules.Tenancy.Api/IfMatchHeader.cs</c> y las privadas de
/// Customers, Shopify, Integrations y Loans): Purchases.Api no referencia esos módulos. Si cambias cómo se lee el header, cambia
/// todas.
/// </summary>
internal static class PurchasesIfMatch
{
    /// <param name="resource">Lo que nombra el mensaje del 428: «purchase order».</param>
    public static long RequireVersion(HttpContext httpContext, string resource) =>
        TryParseVersion(httpContext.Request.Headers.IfMatch, out var version)
            ? version
            : throw new PreconditionRequiredException(
                "precondition.if_match_required",
                $"A valid If-Match header containing the loaded {resource} version is required.");

    // Acepta lo que la SPA ya manda en el resto del repo: "7" y también el débil W/"7".
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
