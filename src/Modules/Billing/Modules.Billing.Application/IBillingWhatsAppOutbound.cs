namespace Modules.Billing.Application;

/// <summary>
/// Con quién y en nombre de quién sale un aviso por WhatsApp. <c>BillingCustomerMessage</c> no trae el tenant ni el cliente (el correo no los
/// necesita), pero el hilo de la bandeja sí: se abre en el tenant, ligado al cliente, y su autor es el encargado del perfil de cobro
/// (la fila del mensaje exige un miembro).
/// </summary>
public sealed record BillingWhatsAppContext(Guid TenantId, Guid CustomerId, Guid SentByMemberId);

/// <summary>El aviso por WhatsApp (§3.3); adaptador en Bootstrapper sobre Mensajería y el directorio de conexiones <c>whatsapp-cloud</c> de Integrations.</summary>
public interface IBillingWhatsAppOutbound
{
    Task<BillingSendResult> SendAsync(
        BillingWhatsAppContext context, BillingCustomerMessage message, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Las conexiones activas del tenant; <c>Ready</c> exige las dos plantillas de cobro aprobadas (spec §5).</summary>
    Task<IReadOnlyList<BillingConnectionDto>> ListConnectionsAsync(Guid tenantId, CancellationToken cancellationToken);
}
