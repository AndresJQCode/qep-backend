namespace Modules.Billing.Application;

/// <summary>El email diario al equipo (§3.3, paso 5); adaptador en Bootstrapper sobre <c>IEmailChannel</c>.</summary>
public interface IBillingStaffEmail
{
    Task<BillingSendResult> SendDigestAsync(BillingDigestEmail digest, CancellationToken cancellationToken);
}
