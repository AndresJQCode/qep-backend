namespace Modules.Quotations.Domain;

public readonly record struct QuotationHistoryEntryId(Guid Value)
{
    public static QuotationHistoryEntryId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
