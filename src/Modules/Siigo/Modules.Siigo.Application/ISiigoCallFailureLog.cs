using Modules.Siigo.Domain;

namespace Modules.Siigo.Application;

/// <summary>
/// Una llamada a Siigo que falló, lista para guardarse en <c>siigo.call_failures</c> (spec 2026-11-21). <paramref name="RequestCurl"/> es
/// el <c>curl</c> reproducible de la petición, con el token y la <c>access_key</c> enmascarados; <paramref name="ResponseBody"/>, lo que
/// Siigo contestó (nulo si no hubo respuesta). <paramref name="IdempotencyKey"/> es la de la factura que se intentó crear y
/// <paramref name="SiigoInvoiceId"/> la del sellado: atan la fila a <c>siigo.invoices</c>. Llevan datos del cliente: el <c>ToString</c> no
/// los escribe.
/// </summary>
public sealed record SiigoCallFailureReport(
    Guid ConnectionId,
    SiigoCallOperation Operation,
    string Method,
    string Path,
    int? Status,
    string Reason,
    string? IdempotencyKey,
    string? SiigoInvoiceId,
    string RequestCurl,
    string? ResponseBody)
{
    public override string ToString() => "SiigoCallFailureReport { }";
}

/// <summary>
/// Puerto hacia el registro de fallas de las llamadas a Siigo. Lo llama el cliente de Siigo en cada llamada que falla, lance o no el
/// método que sigue. <b>No propaga sus propios errores</b>: quien lo llama está a punto de devolver el resultado real de Siigo.
/// </summary>
public interface ISiigoCallFailureLog
{
    Task RecordAsync(SiigoCallFailureReport report, CancellationToken cancellationToken);
}
