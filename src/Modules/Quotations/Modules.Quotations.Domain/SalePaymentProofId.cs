namespace Modules.Quotations.Domain;

public readonly record struct SalePaymentProofId(Guid Value)
{
    public static SalePaymentProofId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
