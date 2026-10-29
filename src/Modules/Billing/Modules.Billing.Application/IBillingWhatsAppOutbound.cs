namespace Modules.Billing.Application;

/// <summary>El aviso por WhatsApp (§3.3); adaptador en Bootstrapper sobre Mensajería y el directorio de conexiones <c>whatsapp-cloud</c> de Integrations.</summary>
public interface IBillingWhatsAppOutbound
{
    Task<BillingSendResult> SendAsync(
        BillingCustomerMessage message, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Las conexiones activas del tenant; <c>Ready</c> exige las dos plantillas de cobro aprobadas (spec §5).</summary>
    Task<IReadOnlyList<BillingConnectionDto>> ListConnectionsAsync(Guid tenantId, CancellationToken cancellationToken);
}
