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
        var product = Product.Create(ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty, Now);

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
            ProductId.New(), TenantId, "  Vela de soja  ", "  VS-001  ", ProductDetails.Empty, Now);

        Assert.Equal("Vela de soja", product.Name);
        Assert.Equal("VS-001", product.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsBlankName(string name)
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(ProductId.New(), TenantId, name, "VS-001", ProductDetails.Empty, Now));

        Assert.Equal("catalog.product.name_required", error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsBlankCode(string code)
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(ProductId.New(), TenantId, "Vela de soja", code, ProductDetails.Empty, Now));

        Assert.Equal("catalog.product.code_required", error.Code);
    }

    // Las columnas son varchar(200) y varchar(60). Sin una guarda de dominio, un valor demasiado
    // largo llega a PostgreSQL y vuelve como 500 server.unexpected — la misma forma de defecto
    // por la que se abrió SDD-CT-06.
    [Fact]
    public void CreateRejectsNameOverTwoHundredCharacters()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(ProductId.New(), TenantId, new string('a', 201), "VS-001", ProductDetails.Empty, Now));

        Assert.Equal("catalog.product.name_too_long", error.Code);
    }

    [Fact]
    public void CreateRejectsCodeOverSixtyCharacters()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(ProductId.New(), TenantId, "Vela de soja", new string('a', 61), ProductDetails.Empty, Now));

        Assert.Equal("catalog.product.code_too_long", error.Code);
    }

    [Fact]
    public void UpdateChangesNameAndCodeAndAdvancesUpdatedAt()
    {
        var product = Product.Create(ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty, Now);
        var later = Now.AddMinutes(5);

        product.Update("Vela de cera", "VC-002", ProductDetails.Empty, later);

        Assert.Equal("Vela de cera", product.Name);
        Assert.Equal("VC-002", product.Code);
        Assert.Equal(later, product.UpdatedAt);
        Assert.Equal(Now, product.CreatedAt);
    }

    [Fact]
    public void UpdateRejectsBlankName()
    {
        var product = Product.Create(ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty, Now);

        var error = Assert.Throws<CatalogDomainException>(() =>
            product.Update("  ", "VS-001", ProductDetails.Empty, Now.AddMinutes(5)));

        Assert.Equal("catalog.product.name_required", error.Code);
    }

    [Fact]
    public void DeactivateTurnsProductInactiveAndAdvancesUpdatedAt()
    {
        var product = Product.Create(ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty, Now);
        var later = Now.AddMinutes(5);

        product.Deactivate(later);

        Assert.False(product.IsActive);
        Assert.Equal(later, product.UpdatedAt);
    }

    // CA-CAT-02-09: inactivar dos veces es un error de negocio, no un éxito silencioso.
    [Fact]
    public void DeactivateRejectsAnAlreadyInactiveProduct()
    {
        var product = Product.Create(ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty, Now);
        product.Deactivate(Now.AddMinutes(5));

        var error = Assert.Throws<CatalogDomainException>(() =>
            product.Deactivate(Now.AddMinutes(10)));

        Assert.Equal("catalog.product.already_inactive", error.Code);
    }

    [Fact]
    public void UpdateRejectsAnInactiveProduct()
    {
        var product = Product.Create(ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty, Now);
        product.Deactivate(Now.AddMinutes(5));

        var error = Assert.Throws<CatalogDomainException>(() =>
            product.Update("Vela de cera", "VC-002", ProductDetails.Empty, Now.AddMinutes(10)));

        Assert.Equal("catalog.product.inactive", error.Code);
    }

    // ---- CAT-04: propiedades nuevas ----
    //
    // Van agrupadas en ProductDetails y no como cinco parametros sueltos de Create/Update: con
    // los cinco sueltos la firma llega a diez argumentos, y sobre todo la invariante
    // precio-y-moneda-van-juntos no tendria donde vivir salvo repetida en los dos metodos.

    // CA-CAT-04-02: los cinco son opcionales. Un producto que no los manda sigue siendo valido.
    [Fact]
    public void CreateWithoutDetailsLeavesThemNull()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty, Now);

        Assert.Null(product.Description);
        Assert.Null(product.ImageFileId);
        Assert.Null(product.Price);
        Assert.Null(product.Currency);
        Assert.Null(product.TaxRateId);
    }

    [Fact]
    public void CreateKeepsTheDetailsItReceives()
    {
        var image = Guid.CreateVersion7();
        var taxRate = TaxRateId.New();

        var product = Product.Create(
            ProductId.New(),
            TenantId,
            "Vela de soja",
            "VS-001",
            ProductDetails.Empty with
            {
                Description = "Cera de soja, 200 g",
                ImageFileId = image,
                Price = 45000m,
                Currency = "COP",
                TaxRateId = taxRate
            },
            Now);

        Assert.Equal("Cera de soja, 200 g", product.Description);
        Assert.Equal(image, product.ImageFileId);
        Assert.Equal(45000m, product.Price);
        Assert.Equal("COP", product.Currency);
        Assert.Equal(taxRate, product.TaxRateId);
    }

    [Fact]
    public void CreateRejectsADescriptionOverTwoThousandCharacters()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(),
                TenantId,
                "Vela de soja",
                "VS-001",
                ProductDetails.Empty with { Description = new string('a', 2001) },
                Now));

        Assert.Equal("catalog.product.description_too_long", error.Code);
    }

    // CA-CAT-04-04. El cero es valido —un producto promocional puede valer 0— asi que la guarda
    // va contra el negativo, no contra el falsy.
    [Fact]
    public void CreateRejectsANegativePrice()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(),
                TenantId,
                "Vela de soja",
                "VS-001",
                ProductDetails.Empty with { Price = -1m, Currency = "COP" },
                Now));

        Assert.Equal("catalog.product.price_negative", error.Code);
    }

    [Fact]
    public void CreateAcceptsAZeroPrice()
    {
        var product = Product.Create(
            ProductId.New(),
            TenantId,
            "Muestra gratis",
            "MG-001",
            ProductDetails.Empty with { Price = 0m, Currency = "COP" },
            Now);

        Assert.Equal(0m, product.Price);
    }

    // CA-CAT-04-05
    [Theory]
    [InlineData("CO")]
    [InlineData("COPX")]
    [InlineData("C0P")]
    public void CreateRejectsACurrencyThatIsNotThreeLetters(string currency)
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(),
                TenantId,
                "Vela de soja",
                "VS-001",
                ProductDetails.Empty with { Price = 1000m, Currency = currency },
                Now));

        Assert.Equal("catalog.product.currency_invalid", error.Code);
    }

    // CA-CAT-04-05, segunda mitad: normalizar es parte del invariante. "cop" y "COP" son la misma
    // moneda, y dejar las dos formas en base obliga a cada consumidor a normalizar de nuevo.
    [Fact]
    public void CreateNormalizesTheCurrencyToUppercase()
    {
        var product = Product.Create(
            ProductId.New(),
            TenantId,
            "Vela de soja",
            "VS-001",
            ProductDetails.Empty with { Price = 1000m, Currency = " cop " },
            Now);

        Assert.Equal("COP", product.Currency);
    }

    // CA-CAT-04-06. Un precio sin moneda es un numero sin unidad, y una moneda sin precio no dice
    // nada. Los dos sentidos, porque una guarda escrita en un solo sentido deja pasar el otro.
    [Fact]
    public void CreateRejectsAPriceWithoutCurrency()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(),
                TenantId,
                "Vela de soja",
                "VS-001",
                ProductDetails.Empty with { Price = 45000m },
                Now));

        Assert.Equal("catalog.product.price_currency_mismatch", error.Code);
    }

    [Fact]
    public void CreateRejectsACurrencyWithoutPrice()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(),
                TenantId,
                "Vela de soja",
                "VS-001",
                ProductDetails.Empty with { Currency = "COP" },
                Now));

        Assert.Equal("catalog.product.price_currency_mismatch", error.Code);
    }

    // CA-CAT-04-03: se puede limpiar, no solo setear. Sin esta prueba, una implementacion que
    // ignore los null "para no pisar" pasa todo lo demas y deja campos imborrables.
    [Fact]
    public void UpdateClearsDetailsThatArePassedAsNull()
    {
        var product = Product.Create(
            ProductId.New(),
            TenantId,
            "Vela de soja",
            "VS-001",
            ProductDetails.Empty with
            {
                Description = "Cera de soja",
                ImageFileId = Guid.CreateVersion7(),
                Price = 45000m,
                Currency = "COP",
                TaxRateId = TaxRateId.New()
            },
            Now);

        product.Update("Vela de soja", "VS-001", ProductDetails.Empty, Now.AddMinutes(5));

        Assert.Null(product.Description);
        Assert.Null(product.ImageFileId);
        Assert.Null(product.Price);
        Assert.Null(product.Currency);
        Assert.Null(product.TaxRateId);
    }

    [Fact]
    public void UpdateAdvancesTheConcurrencyTokenWithDetails()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty, Now);

        product.Update(
            "Vela de soja",
            "VS-001",
            ProductDetails.Empty with { Price = 1000m, Currency = "COP" },
            Now.AddMinutes(5));

        Assert.Equal(2, product.Version);
    }
}
