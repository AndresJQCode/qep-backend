namespace Modules.Catalog.Domain;

public readonly record struct ProductCategoryId(Guid Value)
{
    public static ProductCategoryId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
