using Microsoft.EntityFrameworkCore;
using Modules.Catalog.Infrastructure.Persistence;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Catalog → Wms por variante (spec WMS 2026-10-31 §1.3). Lo que el perfil logístico copia de la variante: el nombre visible que
/// arma <c>CatalogVariantReads</c> (el mismo de las líneas de cotización y del POS), el código, el código de barras, los empaques y
/// si se vende. Un id desconocido o de otro tenant no aparece: es el contrato que usa <c>GetOrCreateDefaultAsync</c> para responder
/// <c>wms.profile.variant_unknown</c>.
/// </summary>
public sealed class WmsCatalogReaderTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // La búsqueda por código contra el esquema real la prueba InitialBalanceImportTests; estas pruebas sólo ejercen GetVariantsAsync, que
    // no toca el contexto, así que alcanza una cadena que no conecta.
    private static CatalogDbContext UnusedCatalog() =>
        new(new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql("Host=localhost;Database=unused").Options);

    private static WmsCatalogReader ReaderFor(VariantWorld world) =>
        new(new FakeProductRepository(world.Product), new FakeOptionDefinitionRepository(world.Size), UnusedCatalog());

    [Fact]
    public async Task AVariantWithOptionsGivesItsDisplayNameCodeBarcodeAndPackagings()
    {
        var world = VariantWorld.Create(TenantId, Now, mediumBarcode: "7701234567890");

        var found = await ReaderFor(world).GetVariantsAsync(TenantId, [world.Medium.Id.Value], Ct);

        var variant = Assert.Single(found);
        Assert.Equal(world.Medium.Id.Value, variant.VariantId);
        Assert.Equal(world.Product.Id.Value, variant.ProductId);
        Assert.Equal("Camiseta básica", variant.ProductName);
        Assert.Equal("Camiseta básica – M", variant.VariantName);
        Assert.Equal("CAM-M", variant.Code);
        Assert.Equal("7701234567890", variant.Barcode);
        Assert.Equal([12], variant.PackagingUnits);
        Assert.True(variant.IsActive);
    }

    [Fact]
    public async Task AnUnknownVariantDoesNotAppear()
    {
        var world = VariantWorld.Create(TenantId, Now);

        var found = await ReaderFor(world).GetVariantsAsync(TenantId, [world.Medium.Id.Value, Guid.CreateVersion7()], Ct);

        Assert.Equal(world.Medium.Id.Value, Assert.Single(found).VariantId);
    }

    [Fact]
    public async Task AVariantOfAnotherTenantDoesNotAppear()
    {
        var world = VariantWorld.Create(TenantId, Now);

        var found = await ReaderFor(world).GetVariantsAsync(Guid.CreateVersion7(), [world.Medium.Id.Value], Ct);

        Assert.Empty(found);
    }

    [Fact]
    public async Task NoIdsMeansNoLookup()
    {
        var world = VariantWorld.Create(TenantId, Now);
        var options = new FakeOptionDefinitionRepository(world.Size);
        var reader = new WmsCatalogReader(new FakeProductRepository(world.Product), options, UnusedCatalog());

        Assert.Empty(await reader.GetVariantsAsync(TenantId, [], Ct));
        Assert.Equal(0, options.Calls);
    }

    [Fact]
    public async Task AnInactiveProductGivesIsActiveFalse()
    {
        var world = VariantWorld.Create(TenantId, Now);
        world.Product.Deactivate(Now);

        var found = await ReaderFor(world).GetVariantsAsync(TenantId, [world.Medium.Id.Value], Ct);

        Assert.False(Assert.Single(found).IsActive);
    }

    [Fact]
    public async Task AnInactiveVariantGivesIsActiveFalse()
    {
        var world = VariantWorld.Create(TenantId, Now, largeIsActive: false);

        var found = await ReaderFor(world).GetVariantsAsync(TenantId, [world.Large.Id.Value], Ct);

        Assert.False(Assert.Single(found).IsActive);
    }
}
