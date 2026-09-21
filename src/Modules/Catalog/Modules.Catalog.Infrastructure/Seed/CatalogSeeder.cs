using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Domain;
using Modules.Catalog.Infrastructure.Persistence;

namespace Modules.Catalog.Infrastructure.Seed;

/// <summary>
/// La mitad de Catalog de la semilla de arranque. Construye los agregados con
/// <c>TaxRate.Create</c> y <c>Product.Create</c> —cada producto con sus cinco escalas de
/// precio por cantidad—, así que todos los invariantes del dominio siguen valiendo — lo único
/// que se saltea respecto de un POST es la capa HTTP.
///
/// Idempotente por código de producto y por nombre de tasa, mismo criterio que
/// <c>GeographySeeder</c> con <c>DivipolaCode</c>. Sólo crea: un producto que ya existe no se
/// toca, ni siquiera para completarle escalas que le falten.
/// </summary>
public static class CatalogSeeder
{
    private const string ResourceSuffix = "Seed.Data.catalog-products.json";

    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public static async Task SeedCatalogAsync(
        this IServiceProvider services,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var seed = ReadSeedFile();
        var now = DateTimeOffset.UtcNow;

        var taxRate = await dbContext.TaxRates.SingleOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.Name == seed.TaxRate.Name,
            cancellationToken);
        if (taxRate is null)
        {
            taxRate = TaxRate.Create(
                TaxRateId.New(), tenantId, seed.TaxRate.Name, seed.TaxRate.Percentage, now);
            dbContext.TaxRates.Add(taxRate);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var existingCodes = await dbContext.Products
            .Where(product => product.TenantId == tenantId)
            .Select(product => product.Code)
            .ToListAsync(cancellationToken);
        var existing = new HashSet<string>(existingCodes, StringComparer.Ordinal);

        var added = false;
        foreach (var product in seed.Products)
        {
            if (!existing.Add(product.Sku))
            {
                continue;
            }

            dbContext.Products.Add(Product.Create(
                ProductId.New(),
                tenantId,
                product.Name,
                product.Sku,
                new ProductDetails { TaxRateId = taxRate.Id },
                new ProductPricing
                {
                    BaseUsd = product.PriceUsd,
                    BaseCop = product.PriceCop,
                    Scales = product.Scales
                        .Select(scale => ToPriceScaleInput(product, scale))
                        .ToList(),
                },
                now));
            added = true;
        }

        if (added)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    // El seeder hace de cliente: PriceScale.Create no calcula los finales, espera que quien
    // manda la escala los traiga y sólo los valida contra base × (1 − descuento%). Se calculan
    // con el mismo PriceScale.FinalFor que usa esa validación, así que no pueden discrepar.
    private static PriceScaleInput ToPriceScaleInput(
        CatalogSeedProduct product, CatalogSeedScale scale)
    {
        var restriction = scale.Restriction switch
        {
            "multiple" => PriceScaleRestriction.Multiple,
            "packaging_unit" => PriceScaleRestriction.PackagingUnit,
            _ => throw new InvalidOperationException(
                $"Catalog seed product '{product.Sku}' has a price scale with the unknown "
                + $"restriction '{scale.Restriction}'; expected 'multiple' or 'packaging_unit'."),
        };

        return new PriceScaleInput(
            scale.FromUnit,
            scale.ToUnit,
            scale.Discount,
            restriction,
            scale.Multiple,
            scale.PackagingUnit,
            PriceScale.FinalFor(product.PriceUsd, scale.Discount),
            PriceScale.FinalFor(product.PriceCop, scale.Discount),
            scale.AllowGrouping);
    }

    // internal y no private: CatalogSeedFileTests lo llama directo, via InternalsVisibleTo.
    internal static CatalogSeedFile ReadSeedFile()
    {
        var assembly = typeof(CatalogSeeder).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Embedded catalog seed resource ending with '{ResourceSuffix}' was not found "
                + $"in assembly '{assembly.FullName}'.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded catalog seed resource '{resourceName}' could not be opened.");

        return JsonSerializer.Deserialize<CatalogSeedFile>(stream, SerializerOptions)
            ?? throw new InvalidOperationException(
                $"Embedded catalog seed resource '{resourceName}' deserialized to null.");
    }
}
