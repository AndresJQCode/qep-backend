namespace Modules.Quotations.Domain;

public readonly record struct CarrierId(Guid Value)
{
    public static CarrierId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
