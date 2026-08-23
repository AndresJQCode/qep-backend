namespace Modules.Customers.Domain;

public readonly record struct CustomerPriceListId(Guid Value)
{
    public static CustomerPriceListId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
