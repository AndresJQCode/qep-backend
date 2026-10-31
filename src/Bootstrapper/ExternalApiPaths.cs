using Microsoft.AspNetCore.Http;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-10-30 §5.5: el prefijo de la API externa, en un solo lugar para que las dos excepciones que cuelgan de él
/// —la de CSRF (<c>RequireCsrfHeaderMiddleware</c>) y la del log de fallas (<c>RequestFailureCapture</c>)— no se separen.
/// Misma regla que <see cref="WebhookPaths"/>: ordinal y con la barra final, así que <c>/api/v1/externalx</c> y
/// <c>/API/v1/external/…</c> no entran.
/// </summary>
public static class ExternalApiPaths
{
    public const string Prefix = "/api/v1/external/";

    public static bool IsExternal(PathString path) =>
        path.Value?.StartsWith(Prefix, StringComparison.Ordinal) == true;
}
