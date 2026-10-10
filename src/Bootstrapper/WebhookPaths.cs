using Microsoft.AspNetCore.Http;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-10-09 §6.7 y §11: el prefijo de los webhooks, en un solo lugar para que las dos excepciones
/// que cuelgan de él —la de CSRF (<c>RequireCsrfHeaderMiddleware</c>) y la del log de fallas
/// (<c>RequestFailureCapture</c>)— no se separen. Ordinal y con la barra final: <c>/api/webhooksx</c> y
/// <c>/API/webhooks/…</c> no entran.
/// </summary>
public static class WebhookPaths
{
    public const string Prefix = "/api/webhooks/";

    public static bool IsWebhook(PathString path) =>
        path.Value?.StartsWith(Prefix, StringComparison.Ordinal) == true;
}
