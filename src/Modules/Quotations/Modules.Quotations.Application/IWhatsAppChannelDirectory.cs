namespace Modules.Quotations.Application;

/// <summary>Las claves de proveedor de los canales (spec 2026-10-21 D17): el mismo texto que la clave del proveedor
/// en Integrations, para que el frontend y la persistencia hablen de lo mismo.</summary>
public static class WhatsAppChannelProviders
{
    public const string Meta = "whatsapp-cloud";

    public const string Zenvia = "zenvia";
}

/// <summary>
/// Una conexión de WhatsApp del tenant lista para enviar (plan 2026-10-21, spec 2026-10-21 D1, D11 y D17). Hay dos
/// variantes, una por proveedor: <see cref="ZenviaWhatsAppChannel"/> y <see cref="MetaWhatsAppChannel"/>.
/// </summary>
public abstract record WhatsAppChannel(Guid ConnectionId, string Name, string? Phone)
{
    public abstract string Provider { get; }
}

/// <summary>
/// Una conexión de Zenvia: la cuenta, el número emisor y sus plantillas. El token viaja en claro, en memoria,
/// sólo para ese envío: <see cref="ToString"/> no lo imprime, y el sender no lo registra.
/// <see cref="OrderTemplateId"/> es opcional en la conexión: sin él, el pedido no se puede enviar.
/// </summary>
public sealed record ZenviaWhatsAppChannel(
    Guid ConnectionId,
    string Name,
    string ApiToken,
    string FromNumber,
    string QuotationTemplateId,
    string? OrderTemplateId)
    : WhatsAppChannel(ConnectionId, Name, FromNumber)
{
    public override string Provider => WhatsAppChannelProviders.Zenvia;

    public override string ToString() =>
        $"ZenviaWhatsAppChannel {{ ConnectionId = {ConnectionId}, QuotationTemplateId = {QuotationTemplateId}, OrderTemplateId = {OrderTemplateId} }}";
}

/// <summary>
/// La conexión de WhatsApp Business (Meta) del tenant. Sin secretos: el envío lo hace Mensajería con la conexión, y
/// aquí sólo viajan la plantilla de la cotización y su idioma. Los pedidos no salen por Meta (spec 2026-10-21 D12).
/// </summary>
public sealed record MetaWhatsAppChannel(
    Guid ConnectionId,
    string Name,
    string? Phone,
    string TemplateName,
    string TemplateLanguage)
    : WhatsAppChannel(ConnectionId, Name, Phone)
{
    public override string Provider => WhatsAppChannelProviders.Meta;
}

/// <summary>
/// Los canales de WhatsApp del tenant. El adaptador vive en Bootstrapper, sobre las conexiones de
/// Integrations (<c>QuotationsWhatsAppChannelDirectory</c>): Quotations no referencia
/// <c>Modules.Integrations.Application</c> (IntegrationsLayerTests).
/// </summary>
public interface IWhatsAppChannelDirectory
{
    /// <summary>Las conexiones del tenant que pueden enviar ahora. Meta: <c>Active</c>, visible y con la plantilla de
    /// cotización <c>APPROVED</c>. Zenvia: como hasta hoy (Active, visibles, con Zenvia habilitado por el operador y con
    /// token, número y plantilla de cotización). Vacía si no hay ninguna.</summary>
    Task<IReadOnlyList<WhatsAppChannel>> ListAvailableAsync(Guid tenantId, CancellationToken cancellationToken);
}
