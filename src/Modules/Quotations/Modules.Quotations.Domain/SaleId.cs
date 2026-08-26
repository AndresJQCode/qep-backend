namespace Modules.Quotations.Domain;

public readonly record struct SaleId(Guid Value)
{
    public static SaleId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
