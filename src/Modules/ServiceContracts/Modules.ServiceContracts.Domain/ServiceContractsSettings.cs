namespace Modules.ServiceContracts.Domain;

/// <summary>
/// Ajustes de cobros del tenant (spec §2, <c>billing.settings</c>): hoy sólo la conexión de
/// WhatsApp de Integrations con la que se mandan los avisos. Una fila por tenant.
/// </summary>
public sealed class ServiceContractsSettings
{
    private ServiceContractsSettings()
    {
    }

    public Guid TenantId { get; private set; }

    /// <summary>FK blanda a una conexión de Integrations; null mientras no se haya elegido.</summary>
    public Guid? WhatsAppConnectionId { get; private set; }

    public int Version { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static ServiceContractsSettings Create(Guid tenantId, DateTimeOffset now) => new()
    {
        TenantId = tenantId,
        Version = 1,
        UpdatedAt = now,
    };

    public void ChangeWhatsAppConnection(Guid? connectionId, DateTimeOffset now)
    {
        WhatsAppConnectionId = connectionId;
        Version++;
        UpdatedAt = now;
    }
}
