namespace Modules.Billing.Application;

/// <summary>El aviso por correo al cliente (§3.3); adaptador en Bootstrapper sobre <c>IEmailChannel</c> y las plantillas de Notifications.</summary>
public interface IBillingCustomerEmail
{
    Task<BillingSendResult> SendAsync(BillingCustomerMessage message, CancellationToken cancellationToken);
}
