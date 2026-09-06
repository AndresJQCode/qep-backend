namespace Modules.Customers.Domain;

public readonly record struct CustomerAddressId(Guid Value)
{
    public static CustomerAddressId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
