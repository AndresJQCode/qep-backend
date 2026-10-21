namespace Modules.Quotations.Application;

/// <summary>
/// Envía la cotización al cliente por WhatsApp — plantilla de Zenvia, pedida por el owner con
/// un `curl` de referencia. A diferencia de <c>IQuotationCustomerLookup</c>, este puerto no
/// cruza a otro módulo: sólo hace una llamada HTTP saliente, así que la implementación vive en
/// la Infrastructure de este mismo módulo (`Whatsapp/ZenviaWhatsAppSender.cs`), sin necesidad
/// de pasar por Bootstrapper.
/// </summary>
public interface IWhatsAppSender
{
    Task SendQuotationAsync(
        WhatsAppQuotationMessage message, CancellationToken cancellationToken);

    /// <summary>Envía el PDF de un pedido. Plantilla propia (<c>Quotations:WhatsApp:OrderTemplateId</c>):
    /// la de la cotización dice «cotización» y lleva <c>valid_until</c>, que un pedido no tiene.</summary>
    Task SendOrderAsync(
        WhatsAppOrderMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Los campos que la plantilla de Zenvia necesita. El PDF viaja como
/// <see cref="DocumentUrl"/>: en la API de Zenvia un template con documento no lleva un
/// contenido aparte de tipo <c>file</c> — la URL entra como una clave más de <c>fields</c>,
/// llamada exactamente <c>documentUrl</c>.
///
/// <see cref="Total"/> y <see cref="ValidUntil"/> viajan crudos, sin formatear: cómo se le
/// muestran a la persona depende del locale de la plantilla, que es configuración del canal —
/// así que el formato es del adaptador, no de este contrato.
/// </summary>
public sealed record WhatsAppQuotationMessage(
    string? ToPhone,
    string FullName,
    string OrderNumber,
    decimal Total,
    /// <summary>ISO code; the sender prints its catalogue symbol and decimals.</summary>
    string Currency,
    DateOnly ValidUntil,
    string DocumentUrl);

/// <summary>
/// Los campos de la plantilla del pedido: los de <see cref="WhatsAppQuotationMessage"/> menos la
/// vigencia, que un pedido no tiene. Mismo criterio que allá: el total viaja crudo y el formato es del
/// adaptador; el PDF va como <see cref="DocumentUrl"/>.
/// </summary>
public sealed record WhatsAppOrderMessage(
    string? ToPhone,
    string FullName,
    string OrderNumber,
    decimal Total,
    /// <summary>ISO code; the sender prints its catalogue symbol and decimals.</summary>
    string Currency,
    string DocumentUrl);
