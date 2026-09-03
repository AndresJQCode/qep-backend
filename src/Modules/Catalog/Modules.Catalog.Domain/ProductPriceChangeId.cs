namespace Modules.Catalog.Domain;

public readonly record struct ProductPriceChangeId(Guid Value)
{
    public static ProductPriceChangeId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
