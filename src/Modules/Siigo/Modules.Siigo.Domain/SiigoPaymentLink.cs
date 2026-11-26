namespace Modules.Siigo.Domain;

/// <summary>
/// Spec 2026-11-18 §5.2: la cuenta de cobro de la empresa (la que el pedido congela en <c>BillingAccount</c>, identificada por
/// <see cref="BankAccountKey"/>) y la forma de pago de Siigo que la representa. <see cref="SiigoPaymentTypeId"/> nulo es «sin
/// vincular». Única por <c>(connection_id, bank_account_key)</c>.
/// </summary>
public sealed class SiigoPaymentLink
{
    private SiigoPaymentLink()
    {
    }

    public SiigoPaymentLink(string bankAccountKey, string? siigoPaymentTypeId)
    {
        BankAccountKey = bankAccountKey;
        SiigoPaymentTypeId = SiigoTaxLink.Normalize(siigoPaymentTypeId);
    }

    public Guid ConnectionId { get; internal set; }

    public Guid TenantId { get; internal set; }

    public string BankAccountKey { get; private set; } = string.Empty;

    public string? SiigoPaymentTypeId { get; private set; }

    internal void Relink(string? siigoPaymentTypeId) => SiigoPaymentTypeId = SiigoTaxLink.Normalize(siigoPaymentTypeId);
}
