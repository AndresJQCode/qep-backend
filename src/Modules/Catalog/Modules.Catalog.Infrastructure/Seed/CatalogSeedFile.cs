namespace Modules.Catalog.Infrastructure.Seed;

// Sólo los campos que se siembran. `_note` y `notSeeded` del archivo se ignoran solos: el
// deserializador descarta lo que no mapea, y esos existen como referencia para quien lea el
// JSON, no como datos.
internal sealed record CatalogSeedFile(
    CatalogSeedTaxRate TaxRate,
    IReadOnlyList<CatalogSeedProduct> Products);

internal sealed record CatalogSeedTaxRate(string Name, int Percentage);

internal sealed record CatalogSeedProduct(
    string Sku,
    string Name,
    decimal? PriceCop,
    decimal? PriceUsd);
