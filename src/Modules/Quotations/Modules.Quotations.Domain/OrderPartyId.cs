namespace Modules.Quotations.Domain;

public readonly record struct OrderPartyId(Guid Value)
{
    public static OrderPartyId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
