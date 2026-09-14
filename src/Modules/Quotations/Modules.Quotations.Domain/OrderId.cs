namespace Modules.Quotations.Domain;

public readonly record struct OrderId(Guid Value)
{
    public static OrderId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
