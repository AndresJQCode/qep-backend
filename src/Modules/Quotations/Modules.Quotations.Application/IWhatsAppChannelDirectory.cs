namespace Modules.Quotations.Application;

/// <summary>
/// Una conexión de Zenvia del tenant lista para enviar: la cuenta, el número emisor y sus plantillas
/// (plan 2026-10-21, Zenvia por tenant; spec 2026-10-21 D1 y D11). El token viaja en claro, en memoria,
/// sólo para ese envío: <see cref="ToString"/> no lo imprime, y el sender no lo registra.
/// <see cref="OrderTemplateId"/> es opcional en la conexión: sin él, el pedido no se puede enviar.
/// </summary>
public sealed record WhatsAppChannel(
    Guid ConnectionId,
    string ApiToken,
    string FromNumber,
    string QuotationTemplateId,
    string? OrderTemplateId)
{
    public override string ToString() =>
        $"WhatsAppChannel {{ ConnectionId = {ConnectionId}, QuotationTemplateId = {QuotationTemplateId}, OrderTemplateId = {OrderTemplateId} }}";
}

/// <summary>
/// Los canales de WhatsApp del tenant. El adaptador vive en Bootstrapper, sobre las conexiones de
/// Integrations (<c>QuotationsWhatsAppChannelDirectory</c>): Quotations no referencia
/// <c>Modules.Integrations.Application</c> (IntegrationsLayerTests).
/// </summary>
public interface IWhatsAppChannelDirectory
{
    /// <summary>Las conexiones de Zenvia del tenant que pueden enviar ahora: Active, visibles, con Zenvia habilitado
    /// por el operador y con token, número y plantilla de cotización. Vacía si no hay ninguna.</summary>
    Task<IReadOnlyList<WhatsAppChannel>> ListAvailableAsync(Guid tenantId, CancellationToken cancellationToken);
}
