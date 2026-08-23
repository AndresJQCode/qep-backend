namespace Modules.Customers.Domain;

public readonly record struct ClientClassificationId(Guid Value)
{
    public static ClientClassificationId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
