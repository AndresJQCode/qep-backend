namespace Modules.Quotations.Domain;

public readonly record struct QuotationPartyId(Guid Value)
{
    public static QuotationPartyId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
