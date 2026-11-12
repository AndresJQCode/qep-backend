using Microsoft.AspNetCore.Http;

namespace Bootstrapper;

/// <summary>
/// Toda respuesta de <c>/api/v1/public/stores/{host}/buyer/…</c> lleva <c>Cache-Control: no-store</c>: tienen cuentas, sesiones y
/// tokens. Va ANTES del limitador, a propósito, para cubrir también lo que no llega al endpoint (el 429, el 401 de la clave, el 400 y
/// el 413 del cuerpo) y lo que sale por excepción. Se fija con <c>OnStarting</c> y no con un set directo: el manejador de excepciones
/// limpia la respuesta, y los callbacks corren al revés de como se registran, así que el de este middleware (registrado antes) es el
/// último en tocar la cabecera.
/// </summary>
public sealed class BuyerNoStoreMiddleware(RequestDelegate next)
{
    private const string BuyerSegment = "buyer";

    public Task InvokeAsync(HttpContext context)
    {
        if (IsBuyerPath(context.Request.Path))
        {
            context.Response.OnStarting(
                static state =>
                {
                    ((HttpResponse)state).Headers.CacheControl = "no-store";
                    return Task.CompletedTask;
                },
                context.Response);
        }

        return next(context);
    }

    // Misma regla ordinal que StorefrontPaths: /api/v1/public/stores/{host}/buyer o /buyer/…
    internal static bool IsBuyerPath(PathString path)
    {
        if (!StorefrontPaths.IsStorefront(path))
        {
            return false;
        }

        var rest = path.Value!.AsSpan(StorefrontPaths.Prefix.Length);
        var slash = rest.IndexOf('/');
        if (slash <= 0)
        {
            return false;
        }

        var afterHost = rest[(slash + 1)..];
        return afterHost.StartsWith(BuyerSegment, StringComparison.Ordinal)
            && (afterHost.Length == BuyerSegment.Length || afterHost[BuyerSegment.Length] == '/');
    }
}
