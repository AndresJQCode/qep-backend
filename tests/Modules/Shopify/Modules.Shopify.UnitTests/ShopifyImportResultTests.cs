using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using static Modules.Shopify.UnitTests.ImporterWorld;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Spec 2026-10-15 §6.2.2 a §6.2.4: lo que el importador hace con el envío, la dirección y el resultado del alta. Lo que ya
/// cubre la tarea 13 (precio, descuento, <c>paymentMethod</c>, notas, <c>Rejected</c> y <c>AlreadyExisted</c>) está en
/// <see cref="ShopifyPreconditionsTests"/> y no se repite acá.
/// </summary>
public sealed class ShopifyImportResultTests
{
    private static readonly Guid AntioquiaId = Guid.Parse("01900000-0000-7000-8000-0000000000f1");
    private static readonly Guid MedellinId = Guid.Parse("01900000-0000-7000-8000-0000000000f2");
    private static readonly ShopifyCarrierRef Servientrega = new(Guid.Parse("01900000-0000-7000-8000-0000000000d1"), "Servientrega", true);
    private static readonly ShopifyCarrierRef Envia = new(Guid.Parse("01900000-0000-7000-8000-0000000000d2"), "Envía", false);

    private static readonly ShopifyAddress Medellin = new(
        "Laura Gómez", "Calle 10 # 43-20", "Apto 301", "Medellín", "Antioquia", "ANT", "CO", "+573001112233");

    private static ShopifyShippingLine Line(decimal amount, string? title = "Servientrega", string? code = "standard") =>
        new(code, title, amount, 0m, false);

    private static ImporterWorld WorldWithMedellin()
    {
        var world = new ImporterWorld();
        world.Geography.Cities[("ANTIOQUIA", "Medellín")] = new ShopifyCityRef(AntioquiaId, MedellinId);
        return world;
    }

    // §6.2.4: total_difference = total de Shopify − total de QEP, y los avisos en el orden del spec. Uno que no cuadra por más
    // del 1 % avisa; la dirección que no resuelve, el cliente incompleto y la transportadora desconocida también.
    [Fact]
    public async Task ImportedSetsTotalDifferenceAndWarnings()
    {
        var world = new ImporterWorld();
        world.Writer.Result = draft => ShopifyOrderWriteResult.Written(Guid.CreateVersion7(), "PED-0009", 100_000m, alreadyExisted: false, draft.ClientId);
        var snapshot = Order(address: Medellin, shippingLines: [Line(19_000m, "Inter Rapidísimo")]);
        var row = world.RowFor(snapshot);

        var outcome = await world.ImportAsync(row, snapshot);

        Assert.Equal(ShopifyImportState.Imported, outcome.State);
        Assert.Equal((decimal?)19_000m, row.TotalDifference);
        Assert.Equal(
            """[{"code":"customer_incomplete","detail":null},{"code":"shipping_address_unresolved","detail":null},{"code":"total_difference","detail":null},{"code":"carrier_unrecognized","detail":{"shippingTitle":"Inter Rapidísimo"}}]""",
            row.Warnings);
    }

    [Fact]
    public async Task CashOnDeliveryWithACarrierThatDoesNotCollectWarnsUnmatched()
    {
        var world = WorldWithMedellin();
        world.Shipping.Carriers["Envía"] = Envia;
        var snapshot = Order(address: Medellin, gateways: ["Contra entrega"], shippingLines: [Line(19_000m, "Envía")]);
        world.Writer.Result = draft => ShopifyOrderWriteResult.Written(Guid.CreateVersion7(), "PED-0009", 119_000m, alreadyExisted: false, draft.ClientId);
        world.Customers.ByEmail["laura@verde.co"] = new ShopifyCustomerRef(Guid.CreateVersion7(), true, true);
        var row = world.RowFor(snapshot);

        await world.ImportAsync(row, snapshot);

        Assert.Equal("""[{"code":"cash_on_delivery_unmatched","detail":{"carrierName":"Envía"}}]""", row.Warnings);
        Assert.True(Assert.Single(world.Writer.Creates).Draft.Shipping!.CashOnDelivery);
    }

    // D-S6: hasta el 1 % del total de Shopify no avisa (el 1 % justo tampoco: «si supera»), pero la diferencia se guarda igual.
    [Theory]
    [InlineData("118000", "1000")]
    [InlineData("117810", "1190")]
    [InlineData("120190", "-1190")]
    public async Task TotalDifferenceUpToOnePercentDoesNotWarn(string qepTotal, string difference)
    {
        var world = WorldWithMedellin();
        world.Customers.ByEmail["laura@verde.co"] = new ShopifyCustomerRef(Guid.CreateVersion7(), true, true);
        world.Writer.Result = draft => ShopifyOrderWriteResult.Written(
            Guid.CreateVersion7(), "PED-0009", decimal.Parse(qepTotal, System.Globalization.CultureInfo.InvariantCulture), alreadyExisted: false, draft.ClientId);
        var snapshot = Order(address: Medellin);
        var row = world.RowFor(snapshot);

        await world.ImportAsync(row, snapshot);

        Assert.Equal(decimal.Parse(difference, System.Globalization.CultureInfo.InvariantCulture), row.TotalDifference);
        Assert.Null(row.Warnings);
    }

    [Fact]
    public async Task TotalDifferenceAboveOnePercentWarnsEitherWay()
    {
        var world = WorldWithMedellin();
        world.Customers.ByEmail["laura@verde.co"] = new ShopifyCustomerRef(Guid.CreateVersion7(), true, true);
        world.Writer.Result = draft => ShopifyOrderWriteResult.Written(Guid.CreateVersion7(), "PED-0009", 120_191m, alreadyExisted: false, draft.ClientId);
        var snapshot = Order(address: Medellin);
        var row = world.RowFor(snapshot);

        await world.ImportAsync(row, snapshot);

        Assert.Equal("""[{"code":"total_difference","detail":null}]""", row.Warnings);
    }

    // D-S26 (H5): con contraentrega el pedido nace pendiente (lo decide el adaptador con la marca del envío) y, si Shopify ya lo
    // da por pagado, el importador sincroniza el pago justo después de crearlo.
    [Fact]
    public async Task CodPaidCreatesPendingThenSyncs()
    {
        var world = WorldWithMedellin();
        var orderId = Guid.CreateVersion7();
        world.Writer.Result = draft => ShopifyOrderWriteResult.Written(orderId, "PED-0010", 119_000m, alreadyExisted: false, draft.ClientId);
        world.Shipping.Carriers["Servientrega"] = Servientrega;
        var snapshot = Order(address: Medellin, financialStatus: "paid", gateways: ["Cash on Delivery (COD)"], shippingLines: [Line(19_000m)]);
        var row = world.RowFor(snapshot);

        await world.ImportAsync(row, snapshot);

        var draft = Assert.Single(world.Writer.Creates).Draft;
        Assert.True(draft.Shipping!.CashOnDelivery);
        Assert.Equal(["create", "sync:paid"], world.Writer.Log);
        Assert.Equal([(TenantId, orderId, "paid")], world.Writer.Syncs);
        Assert.Equal((ShopifyImportState.Imported, (string?)null), (row.ImportState, row.LastError));
    }

    // §6.3: si Quotations no deja sincronizar (ya tiene comprobantes propios, o ya no está pendiente), se audita en last_error y
    // el pedido queda importado igual.
    [Fact]
    public async Task CodSyncIgnoredRecordsLastError()
    {
        var world = WorldWithMedellin();
        world.Writer.SyncOutcome = ShopifyOrderOutcome.Ignored("order.order.payment_has_proofs");
        var snapshot = Order(address: Medellin, financialStatus: "paid", gateways: ["contraentrega"], shippingLines: [Line(19_000m)]);
        var row = world.RowFor(snapshot);

        await world.ImportAsync(row, snapshot);

        Assert.Equal((ShopifyImportState.Imported, "order.order.payment_has_proofs"), (row.ImportState, row.LastError));
    }

    // Ruling S17 con AlreadyExisted: la sincronización del pago que la 13 ya hace tampoco se pierde en silencio si Quotations la
    // rechaza.
    [Fact]
    public async Task AlreadyExistedSyncIgnoredRecordsLastError()
    {
        var world = WorldWithMedellin();
        world.Writer.Result = draft => ShopifyOrderWriteResult.Written(Guid.CreateVersion7(), "PED-0003", 119_000m, alreadyExisted: true, draft.ClientId);
        world.Writer.SyncOutcome = ShopifyOrderOutcome.Ignored("order.order.not_pending");
        var snapshot = Order(address: Medellin, financialStatus: "paid");
        var row = world.RowFor(snapshot);

        await world.ImportAsync(row, snapshot);

        Assert.Equal((ShopifyImportState.Imported, "order.order.not_pending"), (row.ImportState, row.LastError));
    }

    // Un pedido sin contraentrega nace con el estado de Shopify: no hay nada que sincronizar.
    [Fact]
    public async Task PaidWithoutCodDoesNotSync()
    {
        var world = WorldWithMedellin();
        var snapshot = Order(address: Medellin, financialStatus: "paid", gateways: ["Mercado Pago"], shippingLines: [Line(19_000m)]);

        await world.ImportAsync(world.RowFor(snapshot), snapshot);

        Assert.Equal(["create"], world.Writer.Log);
    }

    // §6.2.2 paso 4: el envío cobrado en COP sin IVA del envío en el tenant espera, con el título y el valor; el cliente no se
    // crea (va después de todas las precondiciones) y el pedido no se escribe.
    [Fact]
    public async Task ChargedCopWithoutRateWaitsFreightTaxRateMissing()
    {
        var world = WorldWithMedellin();
        world.Shipping.FreightTaxPercentage = null;
        var snapshot = Order(address: Medellin, shippingLines: [Line(15_000.00m, "Envío")]);
        var row = world.RowFor(snapshot);

        var outcome = await world.ImportAsync(row, snapshot);

        Assert.Equal(ShopifyImportState.Waiting, outcome.State);
        Assert.Equal(
            (ShopifyWaitReasons.FreightTaxRateMissing, """{"shippingTitle":"Envío","amount":15000}"""),
            (row.WaitReason, row.WaitDetail));
        Assert.Equal((1, (DateTimeOffset?)Now), (row.Attempts, row.LastAttemptAt));
        Assert.Empty(world.Customers.Incompletes);
        Assert.Empty(world.Customers.Completes);
        Assert.Empty(world.Writer.Creates);
    }

    // Ronda 1, minor 5: un envío negativo llega al pedido (con la tasa del tenant) para que su regla lo rechace con código, en
    // vez de esconderse como Free; la fila queda order_rejected y la bandeja lo muestra.
    [Fact]
    public async Task NegativeShippingIsRejectedByTheOrderRules()
    {
        var world = WorldWithMedellin();
        world.Writer.Result = draft => draft.Shipping!.FreightAmount < 0
            ? ShopifyOrderWriteResult.Rejected("order.order.freight_amount_invalid")
            : ShopifyOrderWriteResult.Written(Guid.CreateVersion7(), "PED-0011", 119_000m, alreadyExisted: false, draft.ClientId);
        var snapshot = Order(address: Medellin, shippingLines: [Line(-5_000m)]);
        var row = world.RowFor(snapshot);

        await world.ImportAsync(row, snapshot);

        Assert.Equal(
            (ShopifyWaitReasons.OrderRejected, """{"code":"order.order.freight_amount_invalid"}"""),
            (row.WaitReason, row.WaitDetail));
        Assert.Equal(1, world.Shipping.RateRequests);
    }

    // product_unmatched va antes que freight_tax_rate_missing (§6.2).
    [Fact]
    public async Task ProductUnmatchedWinsOverFreightTaxRateMissing()
    {
        var world = WorldWithMedellin();
        world.Shipping.FreightTaxPercentage = null;
        var snapshot = Order(lines: [new ShopifyLine("NO-EXISTE", 1m, 1_000m, 0m)], shippingLines: [Line(15_000m)]);
        var row = world.RowFor(snapshot);

        await world.ImportAsync(row, snapshot);

        Assert.Equal(ShopifyWaitReasons.ProductUnmatched, row.WaitReason);
    }

    // §6.2.2 pasos 3 y 5: la transportadora reconocida y el envío cobrado viajan en el borrador; sin coincidencia, el título va
    // al pedido para «reconocer este nombre» (§6.2.5).
    [Fact]
    public async Task RecognizedCarrierAndChargedShippingGoToTheDraft()
    {
        var world = WorldWithMedellin();
        world.Shipping.Carriers["Servientrega"] = Servientrega;
        var snapshot = Order(address: Medellin, taxesIncluded: false, shippingLines: [Line(10_000m)]);

        await world.ImportAsync(world.RowFor(snapshot), snapshot);

        var draft = Assert.Single(world.Writer.Creates).Draft;
        Assert.Equal(new ShopifyShippingDraft(false, Servientrega.Id, "Servientrega", "Charged", 11_900m, 19, false), draft.Shipping);
        Assert.Null(draft.ExternalShippingTitle);
        Assert.Equal(["Servientrega"], world.Shipping.CarrierRequests);
    }

    [Fact]
    public async Task UnrecognizedCarrierTitleGoesToTheOrder()
    {
        var world = WorldWithMedellin();
        var snapshot = Order(address: Medellin, shippingLines: [Line(19_000m, "Inter Rapidísimo")]);

        await world.ImportAsync(world.RowFor(snapshot), snapshot);

        var draft = Assert.Single(world.Writer.Creates).Draft;
        Assert.Equal(("Inter Rapidísimo", (Guid?)null), (draft.ExternalShippingTitle, draft.Shipping!.CarrierId));
    }

    // §6.2.3 paso 2 (H25): la dirección de Shopify es la parte de envío sólo si queda completa: nombre, teléfono, dirección,
    // departamento y ciudad.
    [Fact]
    public async Task ResolvedAddressBecomesTheShippingParty()
    {
        var world = WorldWithMedellin();
        var snapshot = Order(address: Medellin, shippingLines: [Line(19_000m)]);
        var row = world.RowFor(snapshot);

        await world.ImportAsync(row, snapshot);

        var draft = Assert.Single(world.Writer.Creates).Draft;
        Assert.Equal(
            new ShopifyPartyDraft("Laura Gómez", "+573001112233", "laura@verde.co", "Calle 10 # 43-20 Apto 301", AntioquiaId, MedellinId),
            draft.ShippingParty);
        Assert.Equal("Shopify #1001\nDejar en portería", draft.Notes);
        Assert.DoesNotContain("shipping_address_unresolved", row.Warnings ?? string.Empty, StringComparison.Ordinal);
    }

    // Sin nombre ni teléfono en la dirección, salen del comprador.
    [Fact]
    public async Task ShippingPartyTakesTheBuyerNameAndPhoneWhenTheAddressHasNone()
    {
        var world = WorldWithMedellin();
        var snapshot = Order(address: Medellin with { Name = " ", Phone = null }, shippingLines: [Line(19_000m)]);

        await world.ImportAsync(world.RowFor(snapshot), snapshot);

        var party = Assert.Single(world.Writer.Creates).Draft.ShippingParty;
        Assert.Equal(("Laura Gómez", "+573001234567"), (party!.Name, party.Phone));
    }

    // Si no queda completa, no hay parte de envío: la dirección cruda va a las notas con el aviso shipping_address_unresolved.
    [Fact]
    public async Task UnresolvedAddressGoesToTheNotesWithWarning()
    {
        var world = new ImporterWorld();
        world.Customers.ByEmail["laura@verde.co"] = new ShopifyCustomerRef(Guid.CreateVersion7(), true, true);
        var snapshot = Order(address: Medellin with { City = "Pueblo Perdido" }, shippingLines: [Line(19_000m)]);
        world.Shipping.Carriers["Servientrega"] = Servientrega;
        var row = world.RowFor(snapshot);

        await world.ImportAsync(row, snapshot);

        var draft = Assert.Single(world.Writer.Creates).Draft;
        Assert.Null(draft.ShippingParty);
        Assert.Equal(
            "Shopify #1001\nDejar en portería\nDirección de envío de Shopify: Laura Gómez, Calle 10 # 43-20, Apto 301, Pueblo Perdido, Antioquia, CO, +573001112233",
            draft.Notes);
        Assert.Equal("""[{"code":"shipping_address_unresolved","detail":null}]""", row.Warnings);
    }

    // D-S28: con recogida no hay parte de envío ni aviso de dirección, aunque la dirección no resuelva.
    [Fact]
    public async Task PickupHasNoShippingPartyNorAddressWarning()
    {
        var world = new ImporterWorld();
        world.Customers.ByEmail["laura@verde.co"] = new ShopifyCustomerRef(Guid.CreateVersion7(), true, true);
        var snapshot = Order(
            address: Medellin with { City = "Pueblo Perdido" },
            gateways: ["Contraentrega"],
            shippingLines: [Line(0m, "Recogida en tienda", "pickup")]);
        var row = world.RowFor(snapshot);

        await world.ImportAsync(row, snapshot);

        var draft = Assert.Single(world.Writer.Creates).Draft;
        Assert.Equal((true, false), (draft.Shipping!.IsStorePickup, draft.Shipping.CashOnDelivery));
        Assert.Null(draft.ShippingParty);
        Assert.Equal("Shopify #1001\nDejar en portería", draft.Notes);
        Assert.Null(row.Warnings);
        Assert.Empty(world.Shipping.CarrierRequests);
    }

    // Un pedido sin dirección de envío (p. ej. un producto digital) no tiene parte de envío ni nada que avisar.
    [Fact]
    public async Task NoAddressNoShippingPartyNorWarning()
    {
        var world = new ImporterWorld();
        world.Customers.ByEmail["laura@verde.co"] = new ShopifyCustomerRef(Guid.CreateVersion7(), true, true);
        var snapshot = Order(address: null);
        var row = world.RowFor(snapshot);

        await world.ImportAsync(row, snapshot);

        var draft = Assert.Single(world.Writer.Creates).Draft;
        Assert.Equal((null, new ShopifyShippingDraft(false, null, null, null, 0m, 0, false)), (draft.ShippingParty, draft.Shipping));
        Assert.Null(row.Warnings);
    }
}
