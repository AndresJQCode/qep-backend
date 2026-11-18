namespace Modules.Wholesale.Domain;

public readonly record struct PriceListId(Guid Value)
{
    public static PriceListId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
