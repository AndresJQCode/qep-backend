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
}

/// <summary>
/// Los campos que la plantilla de Zenvia necesita — deliberadamente sólo los básicos del
/// cliente y de la cotización (nombre, dirección, número y id de la cotización). El `curl`
/// original también traía `shippingValue`: se dejó afuera a pedido del owner, porque acá no
/// hay ningún concepto de flete en el dominio de Quotations todavía.
/// </summary>
public sealed record WhatsAppQuotationMessage(
    string? ToPhone,
    string FullName,
    string Address,
    string OrderNumber,
    string OrderId);
