using Microsoft.AspNetCore.Http;

namespace Bootstrapper;

/// <summary>
/// Rutas que llevan un secreto en un segmento: el token de una invitación, de mayorista o de miembros. Lo que la API guarda de un
/// request (<c>platform.request_failures</c> y la línea de log de <c>RequestFailureLoggingMiddleware</c>) usa
/// <see cref="Mask"/> en vez de la ruta cruda, así un <c>accept</c> fallido no deja el token en la base ni en el log (plan
/// mayorista fase 2, Task 7; O-7 y ruling W2). El <c>Instance</c> del ProblemDetails no cambia: vuelve a quien ya tiene el token.
/// Con la barra final y sin distinguir mayúsculas: el enrutamiento de ASP.NET es insensible a ellas, así que <c>/API/V1/…</c>
/// llega al mismo endpoint y también lleva el token.
/// <para>La telemetría usa la misma regla (<see cref="SensitivePathTelemetry"/>): el <c>url.path</c> del span de OpenTelemetry y
/// el scope <c>RequestPath</c> de cada log del request.</para>
/// </summary>
public static class SensitivePaths
{
    public const string WholesaleInvitationPrefix = "/api/v1/wholesale-invitations/";

    public const string MemberInvitationPrefix = "/api/v1/invitations/";

    private const string Masked = "***";

    private static readonly string[] Prefixes = [WholesaleInvitationPrefix, MemberInvitationPrefix];

    /// <summary>La ruta con el segmento que sigue al prefijo cambiado por <c>***</c>; cualquier otra, tal cual.</summary>
    public static string Mask(PathString path) => Mask(path.Value);

    /// <summary>Lo mismo sobre la ruta como texto, como llega en un atributo de span o en el scope <c>RequestPath</c> de un log.</summary>
    public static string Mask(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "/";
        }

        foreach (var prefix in Prefixes)
        {
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rest = value.AsSpan(prefix.Length);
            var end = rest.IndexOf('/');
            var segmentLength = end < 0 ? rest.Length : end;
            return segmentLength == 0
                ? value
                : string.Concat(value.AsSpan(0, prefix.Length), Masked, rest[segmentLength..]);
        }

        return value;
    }
}
