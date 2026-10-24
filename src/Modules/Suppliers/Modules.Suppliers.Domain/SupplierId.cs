namespace Modules.Suppliers.Domain;

public readonly record struct SupplierId(Guid Value)
{
    public static SupplierId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
