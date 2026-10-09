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
    private static readonly ProductPricing ValidPricing = new() { Prices = new Dictionary<string, decimal> { ["USD"] = 1000m } };

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
    // Van agrupadas en ProductDetails y no como parametros sueltos de Create/Update. Price
    // vivió acá hasta CAT-09, que lo retiró por completo — el precio del producto es ahora
    // sólo el de ProductPricing, en USD/COP.

    // CA-CAT-04-02: son opcionales. Un producto que no los manda sigue siendo valido.
    [Fact]
    public void CreateWithoutDetailsLeavesThemNull()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty, ValidPricing, Now);

        Assert.Null(product.Description);
        Assert.Null(product.ImageFileId);
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
                TaxRateId = taxRate
            },
            ValidPricing,
            Now);

        Assert.Equal("Cera de soja, 200 g", product.Description);
        Assert.Equal(image, product.ImageFileId);
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
                TaxRateId = TaxRateId.New()
            },
            ValidPricing,
            Now);

        product.Update("Vela de soja", "VS-001", ProductDetails.Empty, ValidPricing, Now.AddMinutes(5));

        Assert.Null(product.Description);
        Assert.Null(product.ImageFileId);
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

    // ---- Prices as a collection (spec 2026-10-08, D3) ----

    private static ProductPricing PricedIn(params (string Currency, decimal Amount)[] prices) =>
        new() { Prices = prices.ToDictionary(price => price.Currency, price => price.Amount) };

    [Fact]
    public void CreateRejectsAProductWithNoPrice()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
                new ProductPricing(), Now));

        Assert.Equal("catalog.product.price_required", error.Code);
    }

    [Fact]
    public void CreateKeepsOnePricePerCurrency()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
            PricedIn(("COP", 45_000m), ("EUR", 11.4m)), Now);

        Assert.Equal(45_000m, product.PriceIn("COP"));
        Assert.Equal(11.4m, product.PriceIn("EUR"));
        Assert.Null(product.PriceIn("USD"));
        Assert.Equal(["COP", "EUR"], product.PricedCurrencies);
    }

    [Fact]
    public void CreateRejectsANegativePriceInAnyCurrency()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
                PricedIn(("COP", 45_000m), ("EUR", -1m)), Now));

        Assert.Equal("catalog.product.price_negative", error.Code);
    }

    // The application normalises codes (Currencies.Normalize); a lower-case key reaching the
    // aggregate is a programming error, not a 422.
    [Fact]
    public void CreateRejectsACurrencyCodeThatWasNotNormalised()
    {
        Assert.Throws<ArgumentException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
                PricedIn(("eur", 10m)), Now));
    }

    [Fact]
    public void UpdateReplacesThePricesEntirely()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
            PricedIn(("USD", 10m)), Now);

        product.Update("Vela de soja", "VS-001", ProductDetails.Empty, PricedIn(("COP", 45_000m)), Now.AddMinutes(5));

        Assert.Null(product.PriceIn("USD"));
        Assert.Equal(45_000m, product.PriceIn("COP"));
        Assert.Single(product.Prices);
    }

    // The owned rows are keyed (product_id, currency): changing an amount must keep the same row
    // instance, or EF tracks a deleted and an added row with the same key in one SaveChanges.
    [Fact]
    public void UpdateKeepsThePriceRowWhenOnlyTheAmountChanges()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
            PricedIn(("COP", 45_000m)), Now);
        var before = Assert.Single(product.Prices);

        product.Update("Vela de soja", "VS-001", ProductDetails.Empty, PricedIn(("COP", 47_000m)), Now.AddMinutes(5));

        Assert.Same(before, Assert.Single(product.Prices));
        Assert.Equal(47_000m, before.Amount);
    }

    // ---- Escalas de precio ----

    private static PriceScaleInput MultipleScale(
        int fromUnit = 1, int toUnit = 9, decimal discount = 0m, int multiple = 3) =>
        new(fromUnit, toUnit, discount, PriceScaleRestriction.Multiple, multiple);

    [Fact]
    public void CreateAcceptsAProductWithValidScales()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
            new ProductPricing
            {
                Prices = new Dictionary<string, decimal> { ["USD"] = 10m },
                Scales = [MultipleScale()]
            },
            Now);

        var scale = Assert.Single(product.PriceScales);
        Assert.Equal(1, scale.FromUnit);
        Assert.Equal(9, scale.ToUnit);
        Assert.Equal(3, scale.Multiple);
        Assert.Equal(PriceScaleRestriction.Multiple, scale.Restriction);
    }

    private static PriceScaleInput PackagingScale(bool allowGrouping = false) =>
        new(1, 9, 0m, PriceScaleRestriction.PackagingUnit, null, allowGrouping);

    // La escala de empaque no lleva número: usa los empaques del producto.
    [Fact]
    public void CreateAcceptsAPackagingUnitScaleWhenTheProductHasPackagingUnits()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
            new ProductPricing
            {
                Prices = new Dictionary<string, decimal> { ["USD"] = 10m },
                PackagingUnits = [12],
                Scales = [PackagingScale()]
            },
            Now);

        var scale = Assert.Single(product.PriceScales);
        Assert.Equal(PriceScaleRestriction.PackagingUnit, scale.Restriction);
        Assert.Null(scale.Multiple);
        Assert.Equal([12], product.PackagingUnits);
    }

    [Fact]
    public void CreateRejectsAScaleWhereToUnitIsNotGreaterThanFromUnit()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
                new ProductPricing
                {
                    Prices = new Dictionary<string, decimal> { ["USD"] = 10m },
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
                    Prices = new Dictionary<string, decimal> { ["USD"] = 10m },
                    Scales = [MultipleScale(discount: discount)]
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
                    Prices = new Dictionary<string, decimal> { ["USD"] = 10m },
                    Scales = [new PriceScaleInput(1, 9, 0m, null, null)]
                },
                Now));

        Assert.Equal("catalog.product.price_scale.restriction_required", error.Code);
    }

    // Una escala sin restricción sólo la produce la copia (ver PriceScaleCopy). El formulario
    // sigue sin poder guardarla: completar la copia es justamente elegirle una restricción.
    [Fact]
    public void UpdateRejectsAScaleWithoutARestriction()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
            new ProductPricing { Prices = new Dictionary<string, decimal> { ["USD"] = 10m } }, Now);

        var error = Assert.Throws<CatalogDomainException>(() =>
            product.Update(
                "Vela de soja", "VS-001", ProductDetails.Empty,
                new ProductPricing
                {
                    Prices = new Dictionary<string, decimal> { ["USD"] = 10m },
                    Scales = [new PriceScaleInput(1, 9, 0m, null, null)]
                },
                Now.AddMinutes(5)));

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
                    Prices = new Dictionary<string, decimal> { ["USD"] = 10m },
                    Scales = [new PriceScaleInput(1, 9, 0m, PriceScaleRestriction.Multiple, null)]
                },
                Now));

        Assert.Equal("catalog.product.price_scale.multiple_required", error.Code);
    }

    // La agrupación es exclusiva de la restricción Multiple: un empaque no se parte entre
    // productos distintos, así que sumar cajas de A con cajas de B no significa nada.
    [Fact]
    public void CreateRejectsGroupingOnAPackagingUnitRestriction()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
                new ProductPricing
                {
                    Prices = new Dictionary<string, decimal> { ["USD"] = 10m },
                    PackagingUnits = [12],
                    Scales = [PackagingScale(allowGrouping: true)]
                },
                Now));

        Assert.Equal("catalog.product.price_scale.grouping_not_allowed", error.Code);
    }

    [Fact]
    public void CreateKeepsGroupingOnAMultipleRestriction()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
            new ProductPricing
            {
                Prices = new Dictionary<string, decimal> { ["USD"] = 10m },
                Scales =
                [
                    new PriceScaleInput(
                        5, 48, 0m, PriceScaleRestriction.Multiple, 3,
                        AllowGrouping: true)
                ]
            },
            Now);

        Assert.True(Assert.Single(product.PriceScales).AllowGrouping);
    }

    // Sin el flag explícito, una escala no agrupa: es el comportamiento de todas las que ya
    // están guardadas.
    [Fact]
    public void CreateDefaultsGroupingToDisabled()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
            new ProductPricing
            {
                Prices = new Dictionary<string, decimal> { ["USD"] = 10m },
                Scales = [new PriceScaleInput(5, 48, 0m, PriceScaleRestriction.Multiple, 3)]
            },
            Now);

        Assert.False(Assert.Single(product.PriceScales).AllowGrouping);
    }

    [Fact]
    public void CreateRejectsAPackagingUnitRestrictionWithAMultiple()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
                new ProductPricing
                {
                    Prices = new Dictionary<string, decimal> { ["USD"] = 10m },
                    PackagingUnits = [12],
                    Scales = [new PriceScaleInput(1, 9, 0m, PriceScaleRestriction.PackagingUnit, 3)]
                },
                Now));

        Assert.Equal("catalog.product.price_scale.multiple_not_allowed", error.Code);
    }

    [Fact]
    public void UpdateReplacesAllPriceScales()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
            new ProductPricing { Prices = new Dictionary<string, decimal> { ["USD"] = 10m }, Scales = [MultipleScale()] }, Now);
        var originalScaleId = Assert.Single(product.PriceScales).Id;

        product.Update(
            "Vela de soja", "VS-001", ProductDetails.Empty,
            new ProductPricing
            {
                Prices = new Dictionary<string, decimal> { ["USD"] = 10m },
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
            new ProductPricing { Prices = new Dictionary<string, decimal> { ["USD"] = 10m }, Scales = [MultipleScale()] }, Now);

        product.Update(
            "Vela de soja", "VS-001", ProductDetails.Empty,
            new ProductPricing { Prices = new Dictionary<string, decimal> { ["USD"] = 10m } }, Now.AddMinutes(5));

        Assert.Empty(product.PriceScales);
    }

    // ---- Empaques del producto (2026-10-01) ----

    [Fact]
    public void CreateWithoutPackagingUnitsLeavesThemEmpty()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
            ValidPricing, Now);

        Assert.Empty(product.PackagingUnits);
    }

    // Se guardan ascendentes, lleguen como lleguen: es el orden en que la pantalla los pinta y
    // en que se comparan, y no tiene por qué depender de cómo los tecleó el usuario.
    [Fact]
    public void CreateSortsThePackagingUnits()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Keratina 120 ml", "KR-120", ProductDetails.Empty,
            new ProductPricing { Prices = new Dictionary<string, decimal> { ["USD"] = 10m }, PackagingUnits = [150, 100] },
            Now);

        Assert.Equal([100, 150], product.PackagingUnits);
    }

    // Un producto puede declarar sus empaques sin que ninguna escala los exija.
    [Fact]
    public void CreateAcceptsPackagingUnitsWithoutAPackagingScale()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Keratina 120 ml", "KR-120", ProductDetails.Empty,
            new ProductPricing { Prices = new Dictionary<string, decimal> { ["USD"] = 10m }, PackagingUnits = [100, 150], Scales = [MultipleScale()] },
            Now);

        Assert.Equal([100, 150], product.PackagingUnits);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void CreateRejectsAPackagingUnitThatIsNotPositive(int packagingUnit)
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Keratina 120 ml", "KR-120", ProductDetails.Empty,
                new ProductPricing { Prices = new Dictionary<string, decimal> { ["USD"] = 10m }, PackagingUnits = [100, packagingUnit] },
                Now));

        Assert.Equal("catalog.product.packaging_units.invalid", error.Code);
    }

    // El tope por valor es Product.MaxPackagingUnitValue: más allá no hay empaque real, y la regla
    // de Quotations cuenta por restos módulo el empaque menor, así que un número absurdo aquí se
    // vuelve memoria allá.
    [Fact]
    public void CreateRejectsAPackagingUnitAboveTheMaximum()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Keratina 120 ml", "KR-120", ProductDetails.Empty,
                new ProductPricing
                {
                    Prices = new Dictionary<string, decimal> { ["USD"] = 10m },
                    PackagingUnits = [100, Product.MaxPackagingUnitValue + 1]
                },
                Now));

        Assert.Equal("catalog.product.packaging_units.invalid", error.Code);
    }

    [Fact]
    public void CreateAcceptsAPackagingUnitAtTheMaximum()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Keratina 120 ml", "KR-120", ProductDetails.Empty,
            new ProductPricing { Prices = new Dictionary<string, decimal> { ["USD"] = 10m }, PackagingUnits = [Product.MaxPackagingUnitValue] },
            Now);

        Assert.Equal([100_000], product.PackagingUnits);
    }

    // Ningún producto viene en más de un puñado de cajas; el tope (Product.MaxPackagingUnits)
    // acota también lo que la regla de Quotations recorre por cada empaque.
    [Fact]
    public void CreateRejectsMorePackagingUnitsThanTheMaximum()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Keratina 120 ml", "KR-120", ProductDetails.Empty,
                new ProductPricing
                {
                    Prices = new Dictionary<string, decimal> { ["USD"] = 10m },
                    PackagingUnits = Enumerable.Range(1, Product.MaxPackagingUnits + 1).ToArray()
                },
                Now));

        Assert.Equal("catalog.product.packaging_units.too_many", error.Code);
    }

    [Fact]
    public void CreateAcceptsExactlyTheMaximumNumberOfPackagingUnits()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Keratina 120 ml", "KR-120", ProductDetails.Empty,
            new ProductPricing
            {
                Prices = new Dictionary<string, decimal> { ["USD"] = 10m },
                PackagingUnits = Enumerable.Range(1, Product.MaxPackagingUnits).ToArray()
            },
            Now);

        Assert.Equal(10, product.PackagingUnits.Count);
    }

    // Un repetido se rechaza y no se descarta en silencio: quien mandó [100, 100] seguramente
    // quiso escribir otro número, y deduplicarlo le escondería el error.
    [Fact]
    public void CreateRejectsADuplicatedPackagingUnit()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Keratina 120 ml", "KR-120", ProductDetails.Empty,
                new ProductPricing { Prices = new Dictionary<string, decimal> { ["USD"] = 10m }, PackagingUnits = [100, 150, 100] },
                Now));

        Assert.Equal("catalog.product.packaging_units.duplicated", error.Code);
    }

    // La escala de empaque ya no trae número propio: sin empaques en el producto no hay contra
    // qué validarla.
    [Fact]
    public void CreateRejectsAPackagingScaleWhenTheProductHasNoPackagingUnits()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Keratina 120 ml", "KR-120", ProductDetails.Empty,
                new ProductPricing { Prices = new Dictionary<string, decimal> { ["USD"] = 10m }, Scales = [PackagingScale()] },
                Now));

        Assert.Equal("catalog.product.packaging_units_required", error.Code);
    }

    // Y en el PUT igual: vaciar los empaques mientras una escala los usa no se puede.
    [Fact]
    public void UpdateRejectsRemovingThePackagingUnitsOfAPackagingScale()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Keratina 120 ml", "KR-120", ProductDetails.Empty,
            new ProductPricing { Prices = new Dictionary<string, decimal> { ["USD"] = 10m }, PackagingUnits = [100], Scales = [PackagingScale()] },
            Now);

        var error = Assert.Throws<CatalogDomainException>(() =>
            product.Update(
                "Keratina 120 ml", "KR-120", ProductDetails.Empty,
                new ProductPricing { Prices = new Dictionary<string, decimal> { ["USD"] = 10m }, PackagingUnits = [], Scales = [PackagingScale()] },
                Now.AddMinutes(5)));

        Assert.Equal("catalog.product.packaging_units_required", error.Code);
    }

    // El PUT reemplaza el conjunto entero, como las escalas.
    [Fact]
    public void UpdateReplacesThePackagingUnits()
    {
        var product = Product.Create(
            ProductId.New(), TenantId, "Keratina 120 ml", "KR-120", ProductDetails.Empty,
            new ProductPricing { Prices = new Dictionary<string, decimal> { ["USD"] = 10m }, PackagingUnits = [100] },
            Now);

        product.Update(
            "Keratina 120 ml", "KR-120", ProductDetails.Empty,
            new ProductPricing { Prices = new Dictionary<string, decimal> { ["USD"] = 10m }, PackagingUnits = [150, 100], Scales = [PackagingScale()] },
            Now.AddMinutes(5));

        Assert.Equal([100, 150], product.PackagingUnits);
    }
}
