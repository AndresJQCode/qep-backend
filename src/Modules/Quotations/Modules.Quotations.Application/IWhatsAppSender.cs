namespace Modules.Quotations.Application;

/// <summary>
/// Envía el documento al cliente por WhatsApp. Es el Strategy por proveedor (spec 2026-10-21 D17): la
/// implementación (<see cref="WhatsAppSenderRouter"/>) mira el <see cref="WhatsAppChannel"/> que llega en cada
/// envío y lo entrega a Zenvia (<see cref="IZenviaWhatsAppSender"/>) o a Meta (<see cref="IMetaWhatsAppSender"/>).
/// </summary>
public interface IWhatsAppSender
{
    Task SendQuotationAsync(
        WhatsAppChannel channel, WhatsAppQuotationMessage message, CancellationToken cancellationToken);

    /// <summary>Envía el PDF de un pedido. Plantilla propia (<see cref="ZenviaWhatsAppChannel.OrderTemplateId"/>):
    /// la de la cotización dice «cotización» y lleva <c>valid_until</c>, que un pedido no tiene. Sólo por Zenvia.</summary>
    Task SendOrderAsync(
        WhatsAppChannel channel, WhatsAppOrderMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Los campos que la plantilla de Zenvia necesita. El PDF viaja como
/// <see cref="DocumentUrl"/>: en la API de Zenvia un template con documento no lleva un
/// contenido aparte de tipo <c>file</c> — la URL entra como una clave más de <c>fields</c>,
/// llamada exactamente <c>documentUrl</c>.
///
/// <see cref="Total"/> y <see cref="ValidUntil"/> viajan crudos, sin formatear: cómo se le
/// muestran a la persona depende del locale de la plantilla, que es configuración del canal —
/// así que el formato es del adaptador, no de este contrato. Lo mismo vale para el formato numérico
/// del tenant (<see cref="NumberFormat"/>): viaja crudo y el adaptador lo traduce a separadores.
/// </summary>
public sealed record WhatsAppQuotationMessage(
    string? ToPhone,
    string FullName,
    string OrderNumber,
    decimal Total,
    /// <summary>ISO code; the sender prints its catalogue symbol and decimals.</summary>
    string Currency,
    DateOnly ValidUntil,
    /// <summary>Sólo Zenvia: la copia pública del PDF. Meta lee el PDF privado por <see cref="DocumentStorageKey"/>.</summary>
    string? DocumentUrl,
    /// <summary>La clave cruda del formato numérico del tenant; <c>null</c> si no tiene ajustes (el sender cae a colombiano).</summary>
    string? NumberFormat = null,
    /// <summary>La clave privada del PDF en el almacenamiento: Meta la lee desde ahí y no necesita una copia pública.</summary>
    string? DocumentStorageKey = null,
    /// <summary>Quién envía: el mensaje queda en el hilo de Mensajería a su nombre.</summary>
    Guid? SentByMemberId = null,
    /// <summary>El cliente destinatario; <c>null</c> si el mensaje va a los datos de facturación, que no son un cliente.</summary>
    Guid? CustomerId = null,
    /// <summary>El tenant de la cotización: el envío por Meta pasa por Mensajería, que aísla por tenant, y el puerto no recibe más contexto.</summary>
    Guid? TenantId = null);

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
    string? DocumentUrl,
    /// <summary>La clave cruda del formato numérico del tenant; <c>null</c> si no tiene ajustes (el sender cae a colombiano).</summary>
    string? NumberFormat = null,
    /// <summary>La clave privada del PDF en el almacenamiento.</summary>
    string? DocumentStorageKey = null,
    /// <summary>Quién envía.</summary>
    Guid? SentByMemberId = null,
    /// <summary>El cliente destinatario; <c>null</c> si va a los datos de facturación.</summary>
    Guid? CustomerId = null);

/// <summary>El puerto de Zenvia: la plantilla se manda por su API con el token del canal. La implementa la
/// Infrastructure de este módulo (<c>ZenviaWhatsAppSender</c>).</summary>
public interface IZenviaWhatsAppSender
{
    Task SendQuotationAsync(
        ZenviaWhatsAppChannel channel, WhatsAppQuotationMessage message, CancellationToken cancellationToken);

    Task SendOrderAsync(
        ZenviaWhatsAppChannel channel, WhatsAppOrderMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Puerto hacia Mensajería (spec 2026-10-21 D17); el adaptador vive en Bootstrapper porque Quotations no referencia
/// Messaging. Recibe el teléfono ya normalizado: sólo dígitos, con el indicativo.
/// </summary>
public interface IMetaWhatsAppSender
{
    Task SendQuotationAsync(
        MetaWhatsAppChannel channel, string waId, WhatsAppQuotationMessage message, CancellationToken cancellationToken);
}
