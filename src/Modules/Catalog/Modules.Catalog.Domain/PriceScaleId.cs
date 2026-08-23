namespace Modules.Catalog.Domain;

public readonly record struct PriceScaleId(Guid Value)
{
    public static PriceScaleId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
