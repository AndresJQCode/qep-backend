namespace Modules.Quotations.Domain;

public readonly record struct OrderHistoryEntryId(Guid Value)
{
    public static OrderHistoryEntryId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
