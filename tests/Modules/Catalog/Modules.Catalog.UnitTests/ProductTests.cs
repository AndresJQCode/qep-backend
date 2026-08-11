using Modules.Catalog.Domain;

namespace Modules.Catalog.UnitTests;

public sealed class ProductTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now =
        new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateStartsActive()
    {
        var product = Product.Create(ProductId.New(), TenantId, "Vela de soja", "VS-001", Now);

        Assert.True(product.IsActive);
        Assert.Equal(TenantId, product.TenantId);
        Assert.Equal("Vela de soja", product.Name);
        Assert.Equal("VS-001", product.Code);
        Assert.Equal(Now, product.CreatedAt);
        Assert.Equal(Now, product.UpdatedAt);
    }

    // El índice único es sobre (tenant_id, code): " VS-001" y "VS-001" serían dos filas para
    // lo que una persona lee como el mismo código. Normalizar acá mantiene esa decisión en el
    // agregado en vez de dejársela a quien escriba el próximo llamador.
    [Fact]
    public void CreateTrimsNameAndCode()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "  Vela de soja  ", "  VS-001  ", Now);

        Assert.Equal("Vela de soja", product.Name);
        Assert.Equal("VS-001", product.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsBlankName(string name)
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(ProductId.New(), TenantId, name, "VS-001", Now));

        Assert.Equal("catalog.product.name_required", error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsBlankCode(string code)
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(ProductId.New(), TenantId, "Vela de soja", code, Now));

        Assert.Equal("catalog.product.code_required", error.Code);
    }

    // Las columnas son varchar(200) y varchar(60). Sin una guarda de dominio, un valor demasiado
    // largo llega a PostgreSQL y vuelve como 500 server.unexpected — la misma forma de defecto
    // por la que se abrió SDD-CT-06.
    [Fact]
    public void CreateRejectsNameOverTwoHundredCharacters()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(ProductId.New(), TenantId, new string('a', 201), "VS-001", Now));

        Assert.Equal("catalog.product.name_too_long", error.Code);
    }

    [Fact]
    public void CreateRejectsCodeOverSixtyCharacters()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(ProductId.New(), TenantId, "Vela de soja", new string('a', 61), Now));

        Assert.Equal("catalog.product.code_too_long", error.Code);
    }

    [Fact]
    public void UpdateChangesNameAndCodeAndAdvancesUpdatedAt()
    {
        var product = Product.Create(ProductId.New(), TenantId, "Vela de soja", "VS-001", Now);
        var later = Now.AddMinutes(5);

        product.Update("Vela de cera", "VC-002", later);

        Assert.Equal("Vela de cera", product.Name);
        Assert.Equal("VC-002", product.Code);
        Assert.Equal(later, product.UpdatedAt);
        Assert.Equal(Now, product.CreatedAt);
    }

    [Fact]
    public void UpdateRejectsBlankName()
    {
        var product = Product.Create(ProductId.New(), TenantId, "Vela de soja", "VS-001", Now);

        var error = Assert.Throws<CatalogDomainException>(() =>
            product.Update("  ", "VS-001", Now.AddMinutes(5)));

        Assert.Equal("catalog.product.name_required", error.Code);
    }

    [Fact]
    public void DeactivateTurnsProductInactiveAndAdvancesUpdatedAt()
    {
        var product = Product.Create(ProductId.New(), TenantId, "Vela de soja", "VS-001", Now);
        var later = Now.AddMinutes(5);

        product.Deactivate(later);

        Assert.False(product.IsActive);
        Assert.Equal(later, product.UpdatedAt);
    }

    // CA-CAT-02-09: inactivar dos veces es un error de negocio, no un éxito silencioso.
    [Fact]
    public void DeactivateRejectsAnAlreadyInactiveProduct()
    {
        var product = Product.Create(ProductId.New(), TenantId, "Vela de soja", "VS-001", Now);
        product.Deactivate(Now.AddMinutes(5));

        var error = Assert.Throws<CatalogDomainException>(() =>
            product.Deactivate(Now.AddMinutes(10)));

        Assert.Equal("catalog.product.already_inactive", error.Code);
    }

    [Fact]
    public void UpdateRejectsAnInactiveProduct()
    {
        var product = Product.Create(ProductId.New(), TenantId, "Vela de soja", "VS-001", Now);
        product.Deactivate(Now.AddMinutes(5));

        var error = Assert.Throws<CatalogDomainException>(() =>
            product.Update("Vela de cera", "VC-002", Now.AddMinutes(10)));

        Assert.Equal("catalog.product.inactive", error.Code);
    }
}
