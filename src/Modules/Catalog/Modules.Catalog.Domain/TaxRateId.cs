namespace Modules.Catalog.Domain;

public readonly record struct TaxRateId(Guid Value)
{
    public static TaxRateId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
