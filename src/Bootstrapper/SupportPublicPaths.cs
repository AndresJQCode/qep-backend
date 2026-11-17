using Microsoft.AspNetCore.Http;

namespace Bootstrapper;

/// <summary>
/// El prefijo del formulario público de Soporte (plan fase 3, F5), en un solo lugar para las excepciones que cuelgan de él: la de CSRF
/// (<c>RequireCsrfHeaderMiddleware</c>), el <c>no-store</c> (<see cref="BuyerNoStoreMiddleware"/>), el log de fallas
/// (<c>RequestFailureCapture</c>) y el <c>Retry-After</c> del 429. Misma regla que <see cref="StorefrontPaths"/>: ordinal y con la
/// barra final, así que <c>/api/v1/public/supportx/…</c> y <c>/API/v1/public/support/…</c> no entran. Duplica el patrón del grupo
/// de <c>PublicSupportEndpoints</c> porque Bootstrapper no referencia los <c>.Api</c>: <c>SupportPublicPathsTests</c> los ata.
/// </summary>
public static class SupportPublicPaths
{
    public const string Prefix = "/api/v1/public/support/";

    public static bool IsSupportPublic(PathString path) =>
        path.Value?.StartsWith(Prefix, StringComparison.Ordinal) == true;
}
