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
    decimal? PriceUsd,
    IReadOnlyList<CatalogSeedScale> Scales);

// Restriction viaja como texto ("multiple" | "packaging_unit") y lo traduce CatalogSeeder al
// enum del dominio: un literal desconocido tiene que reventar al arrancar con el SKU y el
// valor, no deserializarse a cero en silencio. Los finales no están acá porque el seeder los
// calcula con PriceScale.FinalFor a partir de los precios base del producto.
internal sealed record CatalogSeedScale(
    int FromUnit,
    int ToUnit,
    decimal Discount,
    string Restriction,
    int? Multiple,
    int? PackagingUnit,
    bool AllowGrouping);
