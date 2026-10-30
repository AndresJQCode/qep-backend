using Modules.Billing.Application;

namespace Bootstrapper;

/// <summary>
/// <see cref="IBillingWhatsAppOutbound"/> provisional: sin canal de WhatsApp todo envío falla con <c>channel_not_configured</c>
/// y no hay conexiones que ofrecer. La Task 12 lo reemplaza por el adaptador real sobre Mensajería.
/// </summary>
internal sealed class BillingWhatsAppOutboundUnavailable : IBillingWhatsAppOutbound
{
    public Task<BillingSendResult> SendAsync(
        BillingCustomerMessage message, Guid connectionId, CancellationToken cancellationToken) =>
        Task.FromResult<BillingSendResult>(new BillingSendResult.Failed("channel_not_configured"));

    public Task<IReadOnlyList<BillingConnectionDto>> ListConnectionsAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BillingConnectionDto>>([]);
}
