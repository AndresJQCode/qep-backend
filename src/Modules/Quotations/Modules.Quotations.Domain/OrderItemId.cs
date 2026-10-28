namespace Modules.Quotations.Domain;

public readonly record struct OrderItemId(Guid Value)
{
    public static OrderItemId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
