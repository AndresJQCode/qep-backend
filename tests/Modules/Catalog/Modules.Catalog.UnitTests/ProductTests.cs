using Modules.Catalog.Domain;

namespace Modules.Catalog.UnitTests;

public sealed class ProductTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now =
        new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    // CAT-09 hizo el precio obligatorio: todo producto necesita al menos una moneda. Este
    // helper es lo que usan las pruebas de arriba de CAT-09, a las que no les importa el
    // precio — sólo necesitan una entrada válida para no chocar con esa regla nueva.
    private static readonly ProductPricing ValidPricing = new() { BaseUsd = 1000m, FinalUsd = 1000m };

    [Fact]
    public void CreateStartsActive()
    {
        var product = Product.Create(ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty, ValidPricing, Now);

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
            ProductId.New(), TenantId, "  Vela de soja  ", "  VS-001  ", ProductDetails.Empty, ValidPricing, Now);

        Assert.Equal("Vela de soja", product.Name);
        Assert.Equal("VS-001", product.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsBlankName(string name)
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(ProductId.New(), TenantId, name, "VS-001", ProductDetails.Empty, ValidPricing, Now));

        Assert.Equal("catalog.product.name_required", error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsBlankCode(string code)
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(ProductId.New(), TenantId, "Vela de soja", code, ProductDetails.Empty, ValidPricing, Now));

        Assert.Equal("catalog.product.code_required", error.Code);
    }

    // Las columnas son varchar(200) y varchar(60). Sin una guarda de dominio, un valor demasiado
    // largo llega a PostgreSQL y vuelve como 500 server.unexpected — la misma forma de defecto
    // por la que se abrió SDD-CT-06.
    [Fact]
    public void CreateRejectsNameOverTwoHundredCharacters()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(ProductId.New(), TenantId, new string('a', 201), "VS-001", ProductDetails.Empty, ValidPricing, Now));

        Assert.Equal("catalog.product.name_too_long", error.Code);
    }

    [Fact]
    public void CreateRejectsCodeOverSixtyCharacters()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(ProductId.New(), TenantId, "Vela de soja", new string('a', 61), ProductDetails.Empty, ValidPricing, Now));

        Assert.Equal("catalog.product.code_too_long", error.Code);
    }

    [Fact]
    public void UpdateChangesNameAndCodeAndAdvancesUpdatedAt()
    {
        var product = Product.Create(ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty, ValidPricing, Now);
        var later = Now.AddMinutes(5);

        product.Update("Vela de cera", "VC-002", ProductDetails.Empty, ValidPricing, later);

        Assert.Equal("Vela de cera", product.Name);
        Assert.Equal("VC-002", product.Code);
        Assert.Equal(later, product.UpdatedAt);
        Assert.Equal(Now, product.CreatedAt);
    }

    [Fact]
    public void UpdateRejectsBlankName()
    {
        var product = Product.Create(ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty, ValidPricing, Now);

        var error = Assert.Throws<CatalogDomainException>(() =>
            product.Update("  ", "VS-001", ProductDetails.Empty, ValidPricing, Now.AddMinutes(5)));

        Assert.Equal("catalog.product.name_required", error.Code);
    }

    [Fact]
    public void DeactivateTurnsProductInactiveAndAdvancesUpdatedAt()
    {
        var product = Product.Create(ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty, ValidPricing, Now);
        var later = Now.AddMinutes(5);

        product.Deactivate(later);

        Assert.False(product.IsActive);
        Assert.Equal(later, product.UpdatedAt);
    }

    // CA-CAT-02-09: inactivar dos veces es un error de negocio, no un éxito silencioso.
    [Fact]
    public void DeactivateRejectsAnAlreadyInactiveProduct()
    {
        var product = Product.Create(ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty, ValidPricing, Now);
        product.Deactivate(Now.AddMinutes(5));

        var error = Assert.Throws<CatalogDomainException>(() =>
            product.Deactivate(Now.AddMinutes(10)));

        Assert.Equal("catalog.product.already_inactive", error.Code);
    }

    [Fact]
    public void UpdateRejectsAnInactiveProduct()
    {
        var product = Product.Create(ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty, ValidPricing, Now);
        product.Deactivate(Now.AddMinutes(5));

        var error = Assert.Throws<CatalogDomainException>(() =>
            product.Update("Vela de cera", "VC-002", ProductDetails.Empty, ValidPricing, Now.AddMinutes(10)));

        Assert.Equal("catalog.product.inactive", error.Code);
    }

    // ---- CAT-04: propiedades nuevas ----
    //
    // Van agrupadas en ProductDetails y no como parametros sueltos de Create/Update. Price y
    // Currency vivieron acá hasta CAT-09, que los retiró por completo — el precio del producto
    // es ahora sólo el de ProductPricing, en USD/COP.

    // CA-CAT-04-02: son opcionales. Un producto que no los manda sigue siendo valido.
    [Fact]
    public void CreateWithoutDetailsLeavesThemNull()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty, ValidPricing, Now);

        Assert.Null(product.Description);
        Assert.Null(product.ImageFileId);
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
                Currency = "COP",
                TaxRateId = taxRate
            },
            ValidPricing,
            Now);

        Assert.Equal("Cera de soja, 200 g", product.Description);
        Assert.Equal(image, product.ImageFileId);
        Assert.Equal("COP", product.Currency);
        Assert.Equal(taxRate, product.TaxRateId);
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
                ProductDetails.Empty with { Currency = currency },
                ValidPricing,
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
            ProductDetails.Empty with { Currency = " cop " },
            ValidPricing,
            Now);

        Assert.Equal("COP", product.Currency);
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
                ValidPricing,
                Now));

        Assert.Equal("catalog.product.description_too_long", error.Code);
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
                Currency = "COP",
                TaxRateId = TaxRateId.New()
            },
            ValidPricing,
            Now);

        product.Update("Vela de soja", "VS-001", ProductDetails.Empty, ValidPricing, Now.AddMinutes(5));

        Assert.Null(product.Description);
        Assert.Null(product.ImageFileId);
        Assert.Null(product.Currency);
        Assert.Null(product.TaxRateId);
    }

    [Fact]
    public void UpdateAdvancesTheConcurrencyTokenWithDetails()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty, ValidPricing, Now);

        product.Update(
            "Vela de soja",
            "VS-001",
            ProductDetails.Empty with { Description = "Cera de soja" },
            ValidPricing,
            Now.AddMinutes(5));

        Assert.Equal(2, product.Version);
    }

    // CA-CAT-07-01, en el dominio: activar un producto inactivo lo devuelve a activo y mueve
    // UpdatedAt a la hora de la operacion, no a la de creacion.
    [Fact]
    public void ActivateTurnsProductActiveAndAdvancesUpdatedAt()
    {
        var product = Product.Create(ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty, ValidPricing, Now);
        product.Deactivate(Now.AddMinutes(5));
        var later = Now.AddMinutes(10);

        product.Activate(later);

        Assert.True(product.IsActive);
        Assert.Equal(later, product.UpdatedAt);
    }

    // CA-CAT-07-02: activar algo ya activo es un error de negocio, no un exito silencioso.
    // Espeja DeactivateRejectsAnAlreadyInactiveProduct; el codigo se deriva del que ya existe.
    [Fact]
    public void ActivateRejectsAnAlreadyActiveProduct()
    {
        var product = Product.Create(ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty, ValidPricing, Now);

        var error = Assert.Throws<CatalogDomainException>(() =>
            product.Activate(Now.AddMinutes(5)));

        Assert.Equal("catalog.product.already_active", error.Code);
    }

    // CA-CAT-07-08: Version es el token de concurrencia optimista. Sin el incremento, dos
    // escrituras que se solapan se pisan en silencio y ninguna asercion sobre IsActive lo nota.
    // Create deja 1, Deactivate 2, Activate 3.
    [Fact]
    public void ActivateAdvancesTheConcurrencyToken()
    {
        var product = Product.Create(ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty, ValidPricing, Now);
        product.Deactivate(Now.AddMinutes(5));

        product.Activate(Now.AddMinutes(10));

        Assert.Equal(3, product.Version);
    }

    // CA-CAT-07-03, que es el criterio que justifica el slice: sin esto se puede entregar un
    // Activate que responde bien y deja el producto igual de inservible, porque Update sigue
    // abriendo con EnsureActive().
    [Fact]
    public void ActivateReopensUpdate()
    {
        var product = Product.Create(ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty, ValidPricing, Now);
        product.Deactivate(Now.AddMinutes(5));
        product.Activate(Now.AddMinutes(10));

        product.Update("Vela de coco", "VS-002", ProductDetails.Empty, ValidPricing, Now.AddMinutes(15));

        Assert.Equal("Vela de coco", product.Name);
        Assert.Equal("VS-002", product.Code);
    }

    // ---- CAT-09: precio base/final en USD y COP, y escalas por cantidad ----

    [Fact]
    public void CreateRejectsAProductWithNoPriceInAnyCurrency()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
                new ProductPricing(), Now));

        Assert.Equal("catalog.product.price_base_currency_required", error.Code);
    }

    [Fact]
    public void CreateAcceptsABaseUsdPriceWithoutCop()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
            new ProductPricing { BaseUsd = 10m, FinalUsd = 10m }, Now);

        Assert.Equal(10m, product.PriceBaseUsd);
        Assert.Null(product.PriceBaseCop);
    }

    [Fact]
    public void CreateAcceptsABaseCopPriceWithoutUsd()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
            new ProductPricing { BaseCop = 45000m, FinalCop = 45000m }, Now);

        Assert.Equal(45000m, product.PriceBaseCop);
        Assert.Null(product.PriceBaseUsd);
    }

    [Fact]
    public void CreateRejectsANegativeBaseUsdPrice()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
                new ProductPricing { BaseUsd = -1m }, Now));

        Assert.Equal("catalog.product.price_negative", error.Code);
    }

    [Fact]
    public void CreateRejectsANegativeBaseCopPrice()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
                new ProductPricing { BaseCop = -1m }, Now));

        Assert.Equal("catalog.product.price_negative", error.Code);
    }

    [Fact]
    public void CreateRejectsANegativeFinalUsdPrice()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
                new ProductPricing { BaseUsd = 10m, FinalUsd = -1m }, Now));

        Assert.Equal("catalog.product.price_negative", error.Code);
    }

    [Fact]
    public void CreateRejectsANegativeFinalCopPrice()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
                new ProductPricing { BaseCop = 45000m, FinalCop = -1m }, Now));

        Assert.Equal("catalog.product.price_negative", error.Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void CreateRejectsADiscountOutOfRange(decimal discount)
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
                new ProductPricing { BaseUsd = 10m, FinalUsd = 10m, Discount = discount },
                Now));

        Assert.Equal("catalog.product.discount_out_of_range", error.Code);
    }

    [Fact]
    public void CreateRejectsAFinalUsdPriceWithoutABaseUsdPrice()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
                new ProductPricing { BaseCop = 45000m, FinalCop = 45000m, FinalUsd = 10m },
                Now));

        Assert.Equal("catalog.product.price_final_without_base_usd", error.Code);
    }

    [Fact]
    public void CreateRejectsABaseUsdPriceWithoutItsFinalPrice()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
                new ProductPricing { BaseUsd = 10m },
                Now));

        Assert.Equal("catalog.product.price_final_required_usd", error.Code);
    }

    // El descuento es el mismo para ambas monedas (confirmado por el owner): 10% sobre 10 USD
    // es 9 USD, no cualquier otro valor.
    [Fact]
    public void CreateRejectsAFinalPriceThatDoesNotMatchBaseAndDiscount()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
                new ProductPricing { BaseUsd = 10m, FinalUsd = 8m, Discount = 10m },
                Now));

        Assert.Equal("catalog.product.price_final_mismatch_usd", error.Code);
    }

    [Fact]
    public void CreateAcceptsAFinalPriceThatMatchesBaseAndDiscount()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
            new ProductPricing { BaseUsd = 10m, FinalUsd = 9m, Discount = 10m }, Now);

        Assert.Equal(9m, product.PriceFinalUsd);
        Assert.Equal(10m, product.Discount);
    }

    [Fact]
    public void UpdateReplacesThePricingEntirely()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
            new ProductPricing { BaseUsd = 10m, FinalUsd = 10m }, Now);

        product.Update(
            "Vela de soja", "VS-001", ProductDetails.Empty,
            new ProductPricing { BaseCop = 45000m, FinalCop = 45000m }, Now.AddMinutes(5));

        Assert.Null(product.PriceBaseUsd);
        Assert.Equal(45000m, product.PriceBaseCop);
    }

    // ---- Escalas de precio ----

    private static PriceScaleInput MultipleScale(
        int fromUnit = 1, int toUnit = 9, decimal discount = 0m,
        int multiple = 3, decimal? finalUsd = 10m, decimal? finalCop = null) =>
        new(
            fromUnit, toUnit, discount,
            PriceScaleRestriction.Multiple, multiple, null, finalUsd, finalCop);

    [Fact]
    public void CreateAcceptsAProductWithValidScales()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
            new ProductPricing
            {
                BaseUsd = 10m,
                FinalUsd = 10m,
                Scales = [MultipleScale()]
            },
            Now);

        var scale = Assert.Single(product.PriceScales);
        Assert.Equal(1, scale.FromUnit);
        Assert.Equal(9, scale.ToUnit);
        Assert.Equal(3, scale.Multiple);
        Assert.Null(scale.PackagingUnit);
        Assert.Equal(PriceScaleRestriction.Multiple, scale.Restriction);
    }

    [Fact]
    public void CreateAcceptsAPackagingUnitScale()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
            new ProductPricing
            {
                BaseUsd = 10m,
                FinalUsd = 10m,
                Scales = [new PriceScaleInput(1, 9, 0m, PriceScaleRestriction.PackagingUnit, null, 12, 10m, null)]
            },
            Now);

        var scale = Assert.Single(product.PriceScales);
        Assert.Equal(12, scale.PackagingUnit);
        Assert.Null(scale.Multiple);
    }

    [Fact]
    public void CreateRejectsAScaleWhereToUnitIsNotGreaterThanFromUnit()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
                new ProductPricing
                {
                    BaseUsd = 10m,
                    FinalUsd = 10m,
                    Scales = [MultipleScale(fromUnit: 9, toUnit: 9)]
                },
                Now));

        Assert.Equal("catalog.product.price_scale.range_invalid", error.Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void CreateRejectsAScaleWithADiscountOutOfRange(decimal discount)
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
                new ProductPricing
                {
                    BaseUsd = 10m,
                    FinalUsd = 10m,
                    Scales = [MultipleScale(discount: discount, finalUsd: null)]
                },
                Now));

        Assert.Equal("catalog.product.price_scale.discount_out_of_range", error.Code);
    }

    [Fact]
    public void CreateRejectsAScaleWithoutARestriction()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
                new ProductPricing
                {
                    BaseUsd = 10m,
                    FinalUsd = 10m,
                    Scales = [new PriceScaleInput(1, 9, 0m, null, null, null, 10m, null)]
                },
                Now));

        Assert.Equal("catalog.product.price_scale.restriction_required", error.Code);
    }

    [Fact]
    public void CreateRejectsAMultipleRestrictionWithoutAMultiple()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
                new ProductPricing
                {
                    BaseUsd = 10m,
                    FinalUsd = 10m,
                    Scales = [new PriceScaleInput(1, 9, 0m, PriceScaleRestriction.Multiple, null, null, 10m, null)]
                },
                Now));

        Assert.Equal("catalog.product.price_scale.multiple_required", error.Code);
    }

    [Fact]
    public void CreateRejectsAMultipleRestrictionWithAPackagingUnit()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
                new ProductPricing
                {
                    BaseUsd = 10m,
                    FinalUsd = 10m,
                    Scales = [new PriceScaleInput(1, 9, 0m, PriceScaleRestriction.Multiple, 3, 12, 10m, null)]
                },
                Now));

        Assert.Equal("catalog.product.price_scale.packaging_unit_not_allowed", error.Code);
    }

    [Fact]
    public void CreateRejectsAPackagingUnitRestrictionWithoutAPackagingUnit()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
                new ProductPricing
                {
                    BaseUsd = 10m,
                    FinalUsd = 10m,
                    Scales = [new PriceScaleInput(1, 9, 0m, PriceScaleRestriction.PackagingUnit, null, null, 10m, null)]
                },
                Now));

        Assert.Equal("catalog.product.price_scale.packaging_unit_required", error.Code);
    }

    [Fact]
    public void CreateRejectsAPackagingUnitRestrictionWithAMultiple()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
                new ProductPricing
                {
                    BaseUsd = 10m,
                    FinalUsd = 10m,
                    Scales = [new PriceScaleInput(1, 9, 0m, PriceScaleRestriction.PackagingUnit, 3, 12, 10m, null)]
                },
                Now));

        Assert.Equal("catalog.product.price_scale.multiple_not_allowed", error.Code);
    }

    [Fact]
    public void CreateRejectsAScaleWithNoFinalPriceInAnyCurrency()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
                new ProductPricing
                {
                    BaseUsd = 10m,
                    FinalUsd = 10m,
                    Scales = [MultipleScale(finalUsd: null)]
                },
                Now));

        Assert.Equal("catalog.product.price_scale.final_currency_required", error.Code);
    }

    [Fact]
    public void CreateRejectsAScaleFinalCopWhenTheProductHasNoBaseCop()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
                new ProductPricing
                {
                    BaseUsd = 10m,
                    FinalUsd = 10m,
                    Scales = [MultipleScale(finalUsd: null, finalCop: 45000m)]
                },
                Now));

        Assert.Equal("catalog.product.price_scale.final_without_base_cop", error.Code);
    }

    [Fact]
    public void CreateRejectsAScaleFinalPriceThatDoesNotMatchTheProductBaseAndScaleDiscount()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
                new ProductPricing
                {
                    BaseUsd = 10m,
                    FinalUsd = 10m,
                    Scales = [MultipleScale(discount: 10m, finalUsd: 10m)]
                },
                Now));

        Assert.Equal("catalog.product.price_scale.final_mismatch_usd", error.Code);
    }

    [Fact]
    public void CreateAcceptsAScaleFinalPriceThatMatchesTheProductBaseAndScaleDiscount()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
            new ProductPricing
            {
                BaseUsd = 10m,
                FinalUsd = 10m,
                Scales = [MultipleScale(discount: 10m, finalUsd: 9m)]
            },
            Now);

        Assert.Equal(9m, Assert.Single(product.PriceScales).FinalUsd);
    }

    [Fact]
    public void UpdateReplacesAllPriceScales()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
            new ProductPricing { BaseUsd = 10m, FinalUsd = 10m, Scales = [MultipleScale()] }, Now);
        var originalScaleId = Assert.Single(product.PriceScales).Id;

        product.Update(
            "Vela de soja", "VS-001", ProductDetails.Empty,
            new ProductPricing
            {
                BaseUsd = 10m,
                FinalUsd = 10m,
                Scales = [MultipleScale(fromUnit: 10, toUnit: 20, multiple: 5)]
            },
            Now.AddMinutes(5));

        var replaced = Assert.Single(product.PriceScales);
        Assert.NotEqual(originalScaleId, replaced.Id);
        Assert.Equal(10, replaced.FromUnit);
        Assert.Equal(5, replaced.Multiple);
    }

    [Fact]
    public void UpdateWithNoScalesClearsThemAll()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
            new ProductPricing { BaseUsd = 10m, FinalUsd = 10m, Scales = [MultipleScale()] }, Now);

        product.Update(
            "Vela de soja", "VS-001", ProductDetails.Empty,
            new ProductPricing { BaseUsd = 10m, FinalUsd = 10m }, Now.AddMinutes(5));

        Assert.Empty(product.PriceScales);
    }
}
