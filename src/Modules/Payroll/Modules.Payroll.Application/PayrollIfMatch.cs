using BuildingBlocks.Application;

namespace Modules.Payroll.Application;

/// <summary>
/// El <c>If-Match</c> de una escritura. Llega crudo al caso de uso y se lee <b>después</b> de autorizar: quien opera en otro tenant
/// recibe 403 y no un 428 sobre un encabezado que todavía no puede usar. Sin encabezado válido es 428; la versión vieja es 412 en
/// el caso de uso. <see cref="RequireAllowingZero"/> acepta <c>"0"</c>: la primera escritura de la configuración de una empresa
/// (R-17), cuando todavía no hay fila y el <c>GET</c> responde <c>version: 0</c>.
/// </summary>
internal static class PayrollIfMatch
{
    public static long Require(string? ifMatch) =>
        TryParse(ifMatch, out var version) && version > 0
            ? version
            : throw Missing();

    public static long RequireAllowingZero(string? ifMatch) =>
        TryParse(ifMatch, out var version) && version >= 0
            ? version
            : throw Missing();

    private static PreconditionRequiredException Missing() =>
        new(
            "precondition.if_match_required",
            "A valid If-Match header containing the loaded record version is required.");

    private static bool TryParse(string? etag, out long version)
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
        return long.TryParse(normalized, out version);
    }
}
