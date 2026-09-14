namespace Modules.Quotations.Domain;

public readonly record struct OrderPaymentProofId(Guid Value)
{
    public static OrderPaymentProofId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
