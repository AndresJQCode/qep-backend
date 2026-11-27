using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using static Modules.Shopify.UnitTests.ShopifyEditTestWorld;

namespace Modules.Shopify.UnitTests;

/// <summary>Spec 2026-11-14 §3 y §4: por grupo, aplicar, conflicto (el pedido ya no es la proyección) o eco (el valor nuevo ya es
/// el del pedido); y el cambio entero bloqueado por pedido no editable, pago no permitido o producto sin emparejar.</summary>
public sealed class ShopifyEditPlannerTests
{
    private static ShopifyShippingLine Freight(decimal amount = 15_000m) => new("standard", "Envío estándar", amount, 0m, false);

    [Fact]
    public async Task NoGroupChangedIsAnEmptyPlan()
    {
        var world = new ShopifyEditTestWorld();

        var plan = await world.PlanAsync(world.Applied);

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.Echoes);
        Assert.Null(plan.Draft);
        Assert.Null(plan.BlockReason);
    }

    [Theory]
    [InlineData(ShopifyEditGroupKind.Lines)]
    [InlineData(ShopifyEditGroupKind.Shipping)]
    [InlineData(ShopifyEditGroupKind.ShippingAddress)]
    [InlineData(ShopifyEditGroupKind.Note)]
    [InlineData(ShopifyEditGroupKind.Attributes)]
    public async Task EachGroupAloneIsApplied(ShopifyEditGroupKind kind)
    {
        var world = new ShopifyEditTestWorld();
        var incoming = kind switch
        {
            ShopifyEditGroupKind.Lines => Snapshot(lines: [Line(quantity: 1m)]),
            ShopifyEditGroupKind.Shipping => Snapshot(shippingLines: [Freight()]),
            ShopifyEditGroupKind.ShippingAddress => Snapshot(address: Address with { City = "Envigado" }),
            ShopifyEditGroupKind.Note => Snapshot(note: "B"),
            _ => Snapshot(attributes: new Dictionary<string, string> { ["Regalo"] = "Sí" }),
        };

        var plan = await world.PlanAsync(incoming);

        var group = Assert.Single(plan.Groups);
        Assert.Equal((kind, ShopifyEditGroupOutcome.Apply, (string?)null), (group.Kind, group.Outcome, group.Reason));
        Assert.Null(plan.BlockReason);
        if (kind == ShopifyEditGroupKind.Attributes)
        {
            // Los atributos no viven en el pedido: aplicarlos no lo toca.
            Assert.Null(plan.Draft);
            return;
        }

        Assert.NotNull(plan.Draft);
        Assert.Equal((world.Order.OrderId, world.Order.Version), (plan.Draft.OrderId, plan.Draft.ExpectedVersion));
        Assert.Equal(kind == ShopifyEditGroupKind.Lines, plan.Draft.Lines is not null);
        Assert.Equal(kind == ShopifyEditGroupKind.Shipping, plan.Draft.Shipping is not null);
        Assert.Equal(kind == ShopifyEditGroupKind.ShippingAddress, plan.Draft.ShippingParty is not null);
        Assert.Equal(kind == ShopifyEditGroupKind.Note, plan.Draft.ReplaceBuyerNotes);
    }

    [Fact]
    public async Task AGroupTheAdvisorChangedIsAConflictAndTheRestApplies()
    {
        var world = new ShopifyEditTestWorld();
        var projection = world.Projection;
        world.Order = world.Order with { BuyerNotes = "Nota del asesor" };

        var plan = await world.Planner.PlanAsync(
            world.World.Store, world.ImportedRow(), world.Applied, projection, Snapshot(lines: [Line(quantity: 1m)], note: "B"),
            world.Order, null, TestContext.Current.CancellationToken);

        Assert.Equal(
            [(ShopifyEditGroupKind.Lines, ShopifyEditGroupOutcome.Apply, null), (ShopifyEditGroupKind.Note, ShopifyEditGroupOutcome.Conflict, "qep_changed")],
            plan.Groups.Select(group => (group.Kind, group.Outcome, group.Reason)).ToArray());
        Assert.NotNull(plan.Draft);
        Assert.False(plan.Draft.ReplaceBuyerNotes);
        Assert.Equal(1m, Assert.Single(plan.Draft.Lines!).Quantity);
    }

    [Fact]
    public async Task TheEchoOfTheQepValueIsNotAGroup()
    {
        var world = new ShopifyEditTestWorld();
        var projection = world.Projection;
        world.Order = world.Order with { ShippingParty = Party(EnvigadoId) };

        var plan = await world.Planner.PlanAsync(
            world.World.Store, world.ImportedRow(), world.Applied, projection, Snapshot(address: Address with { City = "Envigado" }),
            world.Order, null, TestContext.Current.CancellationToken);

        Assert.Equal([ShopifyEditGroupKind.ShippingAddress], plan.Echoes);
        Assert.Empty(plan.Groups);
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public async Task AZipOnlyShopifyEditIsAShippingAddressChange()
    {
        var world = new ShopifyEditTestWorld();

        var plan = await world.PlanAsync(Snapshot(address: Address with { Zip = " 050021 " }));

        var group = Assert.Single(plan.Groups);
        Assert.Equal(
            (ShopifyEditGroupKind.ShippingAddress, ShopifyEditGroupOutcome.Apply, (string?)null),
            (group.Kind, group.Outcome, group.Reason));
        Assert.Equal("zip", Assert.Single(group.Rows).Field);
        Assert.Equal("050021", plan.Draft!.ShippingParty!.PostalCode);
    }

    // El eco de un envío sin código postal propio: Shopify conserva el zip que ya tenía, que no es una novedad aunque el pedido
    // no lo guarde.
    [Fact]
    public async Task AZipShopifyAlreadyHadDoesNotCountWhenTheOrderHasNone()
    {
        var world = new ShopifyEditTestWorld();
        world.Applied = Snapshot(address: Address with { Zip = "050021" });
        var projection = world.Projection;
        world.Order = world.Order with { ShippingParty = Party(EnvigadoId) };

        var plan = await world.Planner.PlanAsync(
            world.World.Store, world.ImportedRow(), world.Applied, projection,
            Snapshot(address: Address with { City = "Envigado", Zip = "050021" }),
            world.Order, null, TestContext.Current.CancellationToken);

        Assert.Equal([ShopifyEditGroupKind.ShippingAddress], plan.Echoes);
        Assert.Empty(plan.Groups);
    }

    // Más largo que lo que Quotations guarda: no se recorta (sería otro código) ni frena el pedido.
    [Fact]
    public async Task AZipLongerThanTheOrderStoresDoesNotTravel()
    {
        var world = new ShopifyEditTestWorld();

        var plan = await world.PlanAsync(
            Snapshot(address: Address with { City = "Envigado", Zip = new string('1', ShopifyCityCorrection.PartyPostalCodeMaxLength + 1) }));

        Assert.Null(plan.Draft!.ShippingParty!.PostalCode);
    }

    // Un payload guardado antes de que el espejo leyera el zip no dice cuál tenía Shopify: el que llega no es un cambio.
    [Fact]
    public async Task AZipUnrecordedInTheAppliedVersionIsNotAChange()
    {
        var world = new ShopifyEditTestWorld();
        world.Applied = Snapshot(address: Address with { ZipUnrecorded = true });

        var plan = await world.PlanAsync(Snapshot(address: Address with { Zip = "050021" }));

        Assert.True(plan.IsEmpty);
        Assert.Null(plan.Draft);
    }

    private static readonly ShopifyAddress Madrid = new(
        "Laura Gómez", "Calle Mayor 1", null, "Madrid", "Madrid", "M", "ES", "+34 600 111 222", null, "28013");

    private static async Task<ShopifyEditPlan> PlanOverASpanishPartyAsync(ShopifyAddress incoming)
    {
        var world = new ShopifyEditTestWorld();
        var projection = world.Projection;
        var regionId = Guid.CreateVersion7();
        world.World.Geography.Regions[regionId] = ("ES", "28");
        world.Order = world.Order with
        {
            ShippingParty = new ShopifyShippingFingerprint(
                "Laura Gómez", "600111222", "laura@verde.co", "Calle Mayor 1", null, null, "28013", "ES", "Madrid", regionId),
            ShippingCountry = "ES",
        };
        return await world.Planner.PlanAsync(
            world.World.Store, world.ImportedRow(), world.Applied, projection, Snapshot(address: incoming), world.Order, null,
            TestContext.Current.CancellationToken);
    }

    // El eco de una dirección de fuera de Colombia: lo que Shopify devuelve es lo que el envío mandó.
    [Fact]
    public async Task TheEchoOfAPushedForeignAddressIsNotAGroup()
    {
        var plan = await PlanOverASpanishPartyAsync(Madrid);

        Assert.Equal([ShopifyEditGroupKind.ShippingAddress], plan.Echoes);
        Assert.Empty(plan.Groups);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("address")]
    [InlineData("city")]
    [InlineData("province")]
    [InlineData("country")]
    [InlineData("phone")]
    [InlineData("zip")]
    public async Task AForeignAddressThatIsNotTheEchoStaysAConflict(string changed)
    {
        var incoming = changed switch
        {
            "name" => Madrid with { Name = "Otra Persona" },
            "address" => Madrid with { Address1 = "Calle de Alcalá 20" },
            "city" => Madrid with { City = "Getafe" },
            "province" => Madrid with { ProvinceCode = "B" },
            "country" => Madrid with { CountryCode = "PT" },
            "phone" => Madrid with { Phone = "+34600999888" },
            _ => Madrid with { Zip = "28001" },
        };

        var plan = await PlanOverASpanishPartyAsync(incoming);

        var group = Assert.Single(plan.Groups);
        Assert.Equal(
            (ShopifyEditGroupKind.ShippingAddress, ShopifyEditGroupOutcome.Conflict, "qep_changed"),
            (group.Kind, group.Outcome, group.Reason));
    }

    private static readonly Guid MadridRegionId = Guid.Parse("01900000-0000-7000-8000-0000000000d1");

    private static readonly ShopifyAddress Alcala = Madrid with { Address1 = "Calle de Alcalá 20" };

    private static ShopifyShippingFingerprint SpanishParty(Guid? regionId, string address = "Calle Mayor 1") =>
        new("Laura Gómez", "600111222", "laura@verde.co", address, null, null, "28013", "ES", "Madrid", regionId);

    // imported: lo que QEP escribió desde Shopify (la proyección). current: el pedido de ahora.
    private static async Task<(ShopifyEditTestWorld World, ShopifyEditPlan Plan)> PlanSpanishAsync(
        ShopifyAddress incoming, ShopifyShippingFingerprint? imported, ShopifyShippingFingerprint? current,
        ShopifyAddress? applied = null)
    {
        var world = new ShopifyEditTestWorld();
        world.World.Geography.Regions[MadridRegionId] = ("ES", "28");
        world.Applied = Snapshot(address: applied ?? Madrid);
        world.Order = world.Order with { ShippingParty = imported, ShippingCountry = imported?.Country };
        var projection = world.Projection;
        world.Order = world.Order with { ShippingParty = current, ShippingCountry = current?.Country };
        var plan = await world.Planner.PlanAsync(
            world.World.Store, world.ImportedRow(), world.Applied, projection, Snapshot(address: incoming), world.Order, null,
            TestContext.Current.CancellationToken);
        return (world, plan);
    }

    [Fact]
    public async Task AForeignAddressEditAppliesWhenQepDidNotTouchIt()
    {
        var (_, plan) = await PlanSpanishAsync(Alcala, SpanishParty(MadridRegionId), SpanishParty(MadridRegionId));

        Assert.Equal(ShopifyEditGroupOutcome.Apply, Assert.Single(plan.Groups).Outcome);
        Assert.Equal(
            new ShopifyPartyDraft(
                "Laura Gómez", "+34 600 111 222", "laura@verde.co", "Calle de Alcalá 20", null, null, "28013", "ES", "Madrid",
                MadridRegionId),
            plan.Draft!.ShippingParty);
    }

    [Fact]
    public async Task AForeignAddressEditOverAnAdvisorChangeIsAConflict()
    {
        var (_, plan) = await PlanSpanishAsync(
            Alcala, SpanishParty(MadridRegionId), SpanishParty(MadridRegionId, "Calle Nueva 2"));

        var group = Assert.Single(plan.Groups);
        Assert.Equal((ShopifyEditGroupOutcome.Conflict, "qep_changed"), (group.Outcome, group.Reason));
        Assert.Null(plan.Draft);
    }

    [Fact]
    public async Task AForeignAddressQepAlreadyHasIsAnEcho()
    {
        var (_, plan) = await PlanSpanishAsync(
            Alcala, SpanishParty(MadridRegionId), SpanishParty(MadridRegionId, "Calle de Alcalá 20"));

        Assert.Equal([ShopifyEditGroupKind.ShippingAddress], plan.Echoes);
        Assert.Empty(plan.Groups);
    }

    [Fact]
    public async Task TheEchoOfARegionChosenByTheAdvisorIsNotAGroup()
    {
        // Importado sin región; el asesor la eligió y Shopify devuelve lo que se le mandó.
        var (_, plan) = await PlanSpanishAsync(
            Madrid, SpanishParty(null), SpanishParty(MadridRegionId),
            applied: Madrid with { Province = null, ProvinceCode = null });

        Assert.Equal([ShopifyEditGroupKind.ShippingAddress], plan.Echoes);
        Assert.Empty(plan.Groups);
    }

    [Fact]
    public async Task AForeignAddressAppliesOverAnOrderWithoutParty()
    {
        var (_, plan) = await PlanSpanishAsync(Alcala, imported: null, current: null);

        Assert.Equal(ShopifyEditGroupOutcome.Apply, Assert.Single(plan.Groups).Outcome);
        Assert.Equal("ES", plan.Draft!.ShippingParty!.Country);
    }

    [Fact]
    public async Task AZipOnlyForeignEditKeepsTheRegion()
    {
        var (_, plan) = await PlanSpanishAsync(
            Madrid with { Zip = "28001" }, SpanishParty(MadridRegionId), SpanishParty(MadridRegionId));

        Assert.Equal(
            ("28001", (Guid?)MadridRegionId), (plan.Draft!.ShippingParty!.PostalCode, plan.Draft.ShippingParty.RegionId));
    }

    [Fact]
    public async Task AForeignEditWithoutTranslatableProvinceAppliesWithoutRegion()
    {
        var (_, plan) = await PlanSpanishAsync(
            Alcala with { ProvinceCode = "XX" }, SpanishParty(MadridRegionId), SpanishParty(MadridRegionId));

        Assert.Equal(ShopifyEditGroupOutcome.Apply, Assert.Single(plan.Groups).Outcome);
        Assert.Null(plan.Draft!.ShippingParty!.RegionId);
    }

    [Fact]
    public async Task AForeignEditWithoutCityIsUnresolvedAndNeverReported()
    {
        var (world, plan) = await PlanSpanishAsync(
            Alcala with { City = " " }, SpanishParty(MadridRegionId), SpanishParty(MadridRegionId));

        Assert.Equal("city_unresolved", Assert.Single(plan.Groups).Reason);
        Assert.Empty(world.World.UnresolvedCities.Reports);
    }

    [Fact]
    public async Task ACustomLineEditMatchesThroughItsMapping()
    {
        var world = new ShopifyEditTestWorld();
        var variant = Guid.CreateVersion7();
        world.World.Products.WithVariant(variant);
        world.World.CustomLines.Map(ImporterWorld.ConnectionId, "Garantía", variant);
        var custom = new ShopifyLine(null, 1m, 5_000m, 0m, "Garantía", true, 9003, 1m);

        var plan = await world.PlanAsync(Snapshot(lines: [Line(), custom]));

        Assert.Equal(ShopifyEditGroupOutcome.Apply, Assert.Single(plan.Groups).Outcome);
        Assert.Contains(plan.Draft!.Lines!, line => line.VariantId == variant && line.Quantity == 1m);
    }

    // Spec 2026-11-17 §4: el planificador usa el mismo resolver, así que una línea sin SKU pero vinculada no bloquea.
    [Fact]
    public async Task TheEditPlannerUsesTheLinkToo()
    {
        var world = new ShopifyEditTestWorld();
        var variant = Guid.CreateVersion7();
        world.World.Products.WithVariant(variant, taxPercentage: 19);
        world.World.ProductLinks.Link(
            ImporterWorld.TenantId, ImporterWorld.ConnectionId, 700L, 4455L, world.World.Products.ByVariant[variant].ProductId, variant);

        var plan = await world.PlanAsync(Snapshot(lines: [Line(), new ShopifyLine(null, 1m, 1_000m, 0m, "Gorra", false, 9004, 1m, VariantId: 4455L)]));

        Assert.Null(plan.BlockReason);
        Assert.Contains(plan.Draft!.Lines!, line => line.VariantId == variant && line.Quantity == 1m);
    }

    [Fact]
    public async Task AnUnmatchedLineBlocksTheWholeChange()
    {
        var world = new ShopifyEditTestWorld();

        var plan = await world.PlanAsync(Snapshot(lines: [Line(), new ShopifyLine("NO-EXISTE", 1m, 1_000m, 0m, "Otra", false, 9004, 1m)], note: "B"));

        Assert.Equal("product_unmatched", plan.BlockReason);
        Assert.Null(plan.Draft);
        Assert.False(plan.IsEmpty);
    }

    [Fact]
    public async Task ADocumentOfAnotherCustomerIsCustomerChanged()
    {
        var world = new ShopifyEditTestWorld();
        world.World.Customers.ByDocument[(ShopifyStore.DefaultIdentificationTypeCode, "1036000111")] =
            new ShopifyCustomerRef(Guid.CreateVersion7(), true, true);

        var plan = await world.PlanAsync(
            Snapshot(attributes: new Dictionary<string, string> { [ShopifyStore.DefaultDocumentAttributeKey] = "1036000111" }));

        var buyer = plan.Groups.Single(group => group.Kind == ShopifyEditGroupKind.Buyer);
        Assert.Equal((ShopifyEditGroupOutcome.Conflict, "customer_changed"), (buyer.Outcome, buyer.Reason));
    }

    [Fact]
    public async Task ABuyerChangeIsInformative()
    {
        var world = new ShopifyEditTestWorld();

        var plan = await world.PlanAsync(
            Snapshot() with { Buyer = new ShopifyBuyer("Laura", "Gómez", "nuevo@verde.co", "+573001234567") });

        var buyer = Assert.Single(plan.Groups);
        Assert.Equal((ShopifyEditGroupKind.Buyer, ShopifyEditGroupOutcome.Informative), (buyer.Kind, buyer.Outcome));
        Assert.Null(plan.Draft);
    }

    [Fact]
    public async Task AnUnresolvedCityIsAConflictAndIsReported()
    {
        var world = new ShopifyEditTestWorld();

        var plan = await world.PlanAsync(Snapshot(address: Address with { City = "Bello" }));

        var group = Assert.Single(plan.Groups);
        Assert.Equal((ShopifyEditGroupKind.ShippingAddress, ShopifyEditGroupOutcome.Conflict, "city_unresolved"), (group.Kind, group.Outcome, group.Reason));
        var report = Assert.Single(world.World.UnresolvedCities.Reports);
        Assert.Equal((DepartmentId, "Bello", true, false, OrderId), (report.DepartmentId, report.CityText, report.PartyCityOnly, report.CustomerCityOnly, report.OrderId));
        Assert.Null(plan.Draft);
    }

    [Fact]
    public async Task ACityThroughASynonymApplies()
    {
        var world = new ShopifyEditTestWorld();
        world.World.Geography.Cities[("ANTIOQUIA", "Envigao")] = new ShopifyCityRef(DepartmentId, EnvigadoId, Guid.CreateVersion7());

        var plan = await world.PlanAsync(Snapshot(address: Address with { City = "Envigao" }));

        Assert.Equal(ShopifyEditGroupOutcome.Apply, Assert.Single(plan.Groups).Outcome);
        Assert.Equal((DepartmentId, EnvigadoId), (plan.Draft!.ShippingParty!.DepartmentId, plan.Draft.ShippingParty.CityId));
    }

    [Fact]
    public async Task ThePricesAreShopifysWithoutCatalogDiscounts()
    {
        var world = new ShopifyEditTestWorld();
        world.Applied = Snapshot(taxesIncluded: false);

        var plan = await world.PlanAsync(Snapshot(lines: [Line(quantity: 2m, price: 47_619.05m, discount: 9_523.81m)], taxesIncluded: false));

        var line = Assert.Single(plan.Draft!.Lines!);
        Assert.Equal(ShopifyPricing.UnitPriceWithVat(47_619.05m, false, 19, "COP"), line.UnitPrice);
        Assert.Equal(ShopifyPricing.DiscountPercentage(9_523.81m, 47_619.05m, 2m), line.DiscountPercentage);
        Assert.Equal(10m, line.DiscountPercentage);
    }

    [Fact]
    public async Task AnAddressOnAPickupOrderIsStorePickup()
    {
        var world = new ShopifyEditTestWorld();
        var pickup = new ShopifyShippingLine("pickup-store", "Recoger en tienda", 0m, 0m, false);
        world.Applied = Snapshot(shippingLines: [pickup]);
        world.Order = world.Order with
        {
            IsStorePickup = true,
            ShippingParty = null,
            Shipping = new ShopifyShippingDraft(true, null, null, null, 0m, 0, false),
        };

        var plan = await world.PlanAsync(Snapshot(shippingLines: [pickup], address: Address with { City = "Envigado" }));

        var group = Assert.Single(plan.Groups);
        Assert.Equal((ShopifyEditGroupKind.ShippingAddress, ShopifyEditGroupOutcome.Conflict, "store_pickup"), (group.Kind, group.Outcome, group.Reason));
    }

    [Fact]
    public async Task APickupThatBecomesShippingIsStorePickupChanged()
    {
        var world = new ShopifyEditTestWorld();

        var plan = await world.PlanAsync(Snapshot(shippingLines: [new ShopifyShippingLine("pickup-store", "Recoger en tienda", 0m, 0m, false)]));

        var group = Assert.Single(plan.Groups);
        Assert.Equal((ShopifyEditGroupKind.Shipping, ShopifyEditGroupOutcome.Conflict, "store_pickup_changed"), (group.Kind, group.Outcome, group.Reason));
    }

    [Fact]
    public async Task ANotEditableOrderBlocksBeforeThePaymentStatus()
    {
        var world = new ShopifyEditTestWorld();
        world.Configure(ShopifyEditSync.Manual, ["FullPaymentReceived"]);
        world.Order = world.Order with { IsEditable = false, IsPrepared = true };

        var plan = await world.PlanAsync(Snapshot(note: "B"));

        Assert.Equal("order_not_editable", plan.BlockReason);
        Assert.Null(plan.Draft);
    }

    // Spec 2026-11-17 R7: un pedido de empresa sin IVA se planea con el IVA congelado del pedido. Las líneas sin impuestos de
    // Shopify van a su precio (sin sumar IVA) y llevan la tasa del producto; el envío cobrado va al 0 % aunque el tenant no tenga
    // tasa del envío.
    [Fact]
    public async Task AnOrderWithoutVatPlansLinesAndFreightWithoutVat()
    {
        var world = new ShopifyEditTestWorld();
        world.World.Shipping.FreightTaxPercentage = null;
        world.Applied = Snapshot(taxesIncluded: false);
        world.Order = world.Order with { ChargesVat = false };

        var plan = await world.PlanAsync(Snapshot(lines: [Line(quantity: 3m)], shippingLines: [Freight()], taxesIncluded: false));

        Assert.All(plan.Groups, group => Assert.Equal(ShopifyEditGroupOutcome.Apply, group.Outcome));
        Assert.Equal(
            [ShopifyEditGroupKind.Lines, ShopifyEditGroupKind.Shipping],
            plan.Groups.Select(group => group.Kind).Order());
        var line = Assert.Single(plan.Draft!.Lines!);
        Assert.Equal((50_000m, 19, 3m), (line.UnitPrice, line.TaxPercentage, line.Quantity));
        Assert.Equal((15_000m, 0), (plan.Draft.Shipping!.FreightAmount, plan.Draft.Shipping.FreightTaxPercentage));
    }
}
