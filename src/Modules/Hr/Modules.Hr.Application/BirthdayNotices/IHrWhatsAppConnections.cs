namespace Modules.Hr.Application.BirthdayNotices;

/// <summary>
/// Port to the WhatsApp connections of a tenant (implemented in Bootstrapper over the Integrations connection directory). HR only
/// needs to know that an id is an active WhatsApp connection of that tenant; it knows nothing else about WhatsApp.
/// </summary>
public interface IHrWhatsAppConnections
{
    Task<bool> IsWhatsAppConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
}
