using Microsoft.AspNetCore.Http;

namespace Bootstrapper;

/// <summary>
/// El prefijo de las rutas públicas de la tienda, en un solo lugar para que las excepciones que cuelgan de él —la de CSRF
/// (<c>RequireCsrfHeaderMiddleware</c>) y la del log de fallas (<c>RequestFailureCapture</c>)— no se separen. Misma regla que
/// <see cref="ExternalApiPaths"/>: ordinal y con la barra final, así que <c>/api/v1/public/storesx/…</c> y
/// <c>/API/v1/public/stores/…</c> no entran. Duplica la ruta del grupo de <c>PublicStoreEndpoints</c> porque Bootstrapper no
/// referencia los <c>.Api</c>.
/// </summary>
public static class StorefrontPaths
{
    public const string Prefix = "/api/v1/public/stores/";

    public static bool IsStorefront(PathString path) =>
        path.Value?.StartsWith(Prefix, StringComparison.Ordinal) == true;
}
