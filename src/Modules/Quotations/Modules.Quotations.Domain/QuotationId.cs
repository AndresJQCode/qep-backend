namespace Modules.Quotations.Domain;

public readonly record struct QuotationId(Guid Value)
{
    public static QuotationId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
