namespace Modules.Quotations.Domain;

public readonly record struct QuotationItemId(Guid Value)
{
    public static QuotationItemId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
