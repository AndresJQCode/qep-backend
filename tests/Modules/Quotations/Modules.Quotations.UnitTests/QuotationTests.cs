using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

public sealed class QuotationTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly ValidUntil = new(2026, 9, 30);

    private static readonly QuotationBillingAccount BillingAccount = new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "12345678",
        Currency = "COP",
    };

    private static Quotation NewQuotation(
        string? notes = null,
        QuotationParties? parties = null,
        QuotationBillingAccount? billingAccount = null,
        bool customerWithRetention = false,
        bool customerVatSurplus = false,
        DateOnly? validUntil = null) =>
        Quotation.Create(
            QuotationId.New(),
            TenantId,
            "QUO-2026-0001",
            ClientId,
            AdvisorId,
            validUntil ?? ValidUntil,
            paymentMethod: "Transferencia bancaria",
            notes,
            parties ?? QuotationParties.Empty,
            billingAccount,
            customerWithRetention,
            customerVatSurplus,
            AdvisorId,
            Now);

    [Fact]
    public void CreateStartsAsDraftWithEmptyTotals()
    {
        var quotation = NewQuotation();

        Assert.Equal(QuotationStatus.Draft, quotation.Status);
        Assert.Equal(TenantId, quotation.TenantId);
        Assert.Equal(ClientId, quotation.ClientId);
        Assert.Equal(AdvisorId, quotation.AdvisorId);
        Assert.Equal(AdvisorId, quotation.CreatedBy);
        Assert.Null(quotation.UpdatedBy);
        Assert.Equal(Now, quotation.CreatedAt);
        Assert.Equal(Now, quotation.UpdatedAt);
        Assert.Equal(1, quotation.Version);
        Assert.Empty(quotation.Items);
        Assert.Equal(0m, quotation.Subtotal);
        Assert.Equal(0m, quotation.DiscountAmount);
        Assert.Equal(0m, quotation.TaxPercentage);
        Assert.Equal(0m, quotation.TaxAmount);
        Assert.Equal(0m, quotation.Total);
    }

    [Fact]
    public void CreateTrimsQuotationNumber()
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "  QUO-2026-0001  ", ClientId, AdvisorId,
            null, null, null, QuotationParties.Empty, null, false, false, AdvisorId, Now);

        Assert.Equal("QUO-2026-0001", quotation.QuotationNumber);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsBlankQuotationNumber(string number)
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            Quotation.Create(
                QuotationId.New(), TenantId, number, ClientId, AdvisorId,
                null, null, null, QuotationParties.Empty, null, false, false, AdvisorId, Now));

        Assert.Equal("quotation.quotation.number_required", error.Code);
    }

    [Fact]
    public void CreateRejectsQuotationNumberOverTwentyCharacters()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            Quotation.Create(
                QuotationId.New(), TenantId, new string('a', 21), ClientId, AdvisorId,
                null, null, null, QuotationParties.Empty, null, false, false, AdvisorId, Now));

        Assert.Equal("quotation.quotation.number_too_long", error.Code);
    }

    [Fact]
    public void CreateRejectsEmptyClientId()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            Quotation.Create(
                QuotationId.New(), TenantId, "QUO-2026-0001", Guid.Empty, AdvisorId,
                null, null, null, QuotationParties.Empty, null, false, false, AdvisorId, Now));

        Assert.Equal("quotation.quotation.client_required", error.Code);
    }

    // Escala de ejemplo del propio documento: 10-19 unidades, 5% de descuento.
    [Fact]
    public void AddItemComputesDiscountSubtotalAndTaxFromResolvedPercentages()
    {
        var quotation = NewQuotation();
        var productId = Guid.CreateVersion7();

        quotation.AddItem(
            QuotationItemId.New(), productId, quantity: 10, unitPrice: 119_000m,
            discountPercentage: 5m, taxPercentage: 19, AdvisorId, Now);

        var item = Assert.Single(quotation.Items);
        Assert.Equal(productId, item.ProductId);
        Assert.Equal(10m, item.Quantity);
        Assert.Equal(119_000m, item.UnitPrice);
        Assert.Equal(5m, item.DiscountPercentage);
        // El precio viene con IVA incluido: 10 * 119_000 = 1_190_000; descuento 5% = 59_500;
        // cobrado = 1_130_500.
        Assert.Equal(59_500m, item.DiscountAmount);
        // El IVA se extrae de lo cobrado (x 19/119 = 180_500), no se suma encima, y el
        // subtotal es lo que queda de base: 1_130_500 - 180_500 = 950_000.
        Assert.Equal(950_000m, item.Subtotal);
        Assert.Equal(19, item.TaxPercentage);
        Assert.Equal(180_500m, item.TaxAmount);
        Assert.Equal(1, item.Position);
    }

    [Fact]
    public void AddItemRecalculatesHeaderTotals()
    {
        var quotation = NewQuotation();

        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 10, unitPrice: 119_000m,
            discountPercentage: 5m, taxPercentage: 19, AdvisorId, Now);

        // cobrado = 1_130_500 con IVA adentro; IVA extraido = 180_500; base = 950_000
        Assert.Equal(950_000m, quotation.Subtotal);
        Assert.Equal(59_500m, quotation.DiscountAmount);
        Assert.Equal(180_500m, quotation.TaxAmount);
        Assert.Equal(19m, quotation.TaxPercentage);
        Assert.Equal(1_130_500m, quotation.Total);
        Assert.Equal(2, quotation.Version);
    }

    // El impuesto de la cotización es la suma del de cada línea, no un único porcentaje sobre
    // el subtotal completo (RN-013): dos líneas con tasas distintas prueban justo eso.
    [Fact]
    public void AddItemSumsTaxAcrossLinesWithDifferentRates()
    {
        var quotation = NewQuotation();

        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 119_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 100_000m,
            discountPercentage: 0m, taxPercentage: 0, AdvisorId, Now);

        // línea 1: 119_000 con 19% adentro -> base 100_000, IVA 19_000; línea 2: sin tasa, su
        // precio es todo base. Suma de bases = 200_000, suma de IVA = 19_000.
        Assert.Equal(200_000m, quotation.Subtotal);
        Assert.Equal(19_000m, quotation.TaxAmount);
        // tasa efectiva: 19_000 / 200_000 * 100 = 9.5
        Assert.Equal(9.5m, quotation.TaxPercentage);
    }

    [Fact]
    public void CustomerVatSurplusZeroesOutTheHeaderTaxRegardlessOfLineTaxRates()
    {
        var quotation = NewQuotation(customerVatSurplus: true);

        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 119_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);

        Assert.Equal(100_000m, quotation.Subtotal);
        Assert.Equal(0m, quotation.TaxAmount);
        Assert.Equal(0m, quotation.TaxPercentage);
        Assert.Equal(100_000m, quotation.Total);
        Assert.Equal(0m, quotation.RetentionAmount);
        Assert.Equal(100_000m, quotation.NetTotal);
    }

    [Fact]
    public void CustomerWithRetentionComputesTwoPointFivePercentOfSubtotalAndSubtractsFromNetTotal()
    {
        var quotation = NewQuotation(customerWithRetention: true);

        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 119_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);

        // 119_000 cobrados con el IVA adentro -> base 100_000 + IVA 19_000; la retención es el
        // 2.5% de la base: 2_500.
        Assert.Equal(100_000m, quotation.Subtotal);
        Assert.Equal(119_000m, quotation.Total);
        Assert.Equal(2_500m, quotation.RetentionAmount);
        Assert.Equal(116_500m, quotation.NetTotal);
    }

    [Fact]
    public void WithoutRetentionOrVatSurplusNetTotalEqualsTotalAndRetentionIsZero()
    {
        var quotation = NewQuotation();

        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 119_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);

        Assert.Equal(0m, quotation.RetentionAmount);
        Assert.Equal(quotation.Total, quotation.NetTotal);
    }

    [Fact]
    public void RefreshCustomerTaxProfileUpdatesFlagsAndRecalculatesWhileEditable()
    {
        var quotation = NewQuotation();
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 119_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);

        quotation.RefreshCustomerTaxProfile(customerWithRetention: true, customerVatSurplus: true);

        Assert.True(quotation.CustomerWithRetention);
        Assert.True(quotation.CustomerVatSurplus);
        Assert.Equal(0m, quotation.TaxAmount);
        Assert.Equal(2_500m, quotation.RetentionAmount);
        Assert.Equal(97_500m, quotation.NetTotal);
    }

    [Fact]
    public void RefreshCustomerTaxProfileDoesNothingOnceVoided()
    {
        var quotation = NewQuotation();
        quotation.Void(AdvisorId, Now);

        quotation.RefreshCustomerTaxProfile(customerWithRetention: true, customerVatSurplus: true);

        Assert.False(quotation.CustomerWithRetention);
        Assert.False(quotation.CustomerVatSurplus);
    }

    [Fact]
    public void AddItemRejectsTaxPercentageOutOfRange()
    {
        var quotation = NewQuotation();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.AddItem(
                QuotationItemId.New(), Guid.CreateVersion7(), 1, 1000m, 0m, 101, AdvisorId, Now));

        Assert.Equal("quotation.item.tax_percentage_out_of_range", error.Code);
    }

    [Fact]
    public void AddItemAssignsIncrementingPosition()
    {
        var quotation = NewQuotation();

        quotation.AddItem(QuotationItemId.New(), Guid.CreateVersion7(), 1, 1000m, 0m, 0, AdvisorId, Now);
        quotation.AddItem(QuotationItemId.New(), Guid.CreateVersion7(), 2, 2000m, 0m, 0, AdvisorId, Now);

        Assert.Equal([1, 2], quotation.Items.Select(item => item.Position));
    }

    [Fact]
    public void AddItemZeroPercentDiscountWhenQuantityOutsideAnyScale()
    {
        // Decisión confirmada: cantidad fuera de cualquier escala definida -> 0%, no bloquea.
        var quotation = NewQuotation();

        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 3, unitPrice: 1000m,
            discountPercentage: 0m, taxPercentage: 0, AdvisorId, Now);

        var item = Assert.Single(quotation.Items);
        Assert.Equal(0m, item.DiscountPercentage);
        Assert.Equal(3000m, item.Subtotal);
    }

    [Fact]
    public void AddItemRejectsNonPositiveQuantity()
    {
        var quotation = NewQuotation();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.AddItem(QuotationItemId.New(), Guid.CreateVersion7(), 0, 1000m, 0m, 0, AdvisorId, Now));

        Assert.Equal("quotation.item.quantity_invalid", error.Code);
    }

    [Fact]
    public void AddItemRejectsDiscountOutOfRange()
    {
        var quotation = NewQuotation();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.AddItem(
                QuotationItemId.New(), Guid.CreateVersion7(), 1, 1000m, 100.01m, 0, AdvisorId, Now));

        Assert.Equal("quotation.item.discount_out_of_range", error.Code);
    }

    [Fact]
    public void UpdateItemQuantityRecalculatesLineAndHeaderTotals()
    {
        var quotation = NewQuotation();
        var itemId = QuotationItemId.New();
        quotation.AddItem(itemId, Guid.CreateVersion7(), 5, 100_000m, 0m, 0, AdvisorId, Now);

        // Cantidad sube a 10 -> ahora cae en la escala de 5%.
        quotation.UpdateItemQuantity(itemId, 10, 5m, 0, AdvisorId, Now);

        var item = Assert.Single(quotation.Items);
        Assert.Equal(10m, item.Quantity);
        Assert.Equal(5m, item.DiscountPercentage);
        Assert.Equal(950_000m, item.Subtotal);
        Assert.Equal(950_000m, quotation.Subtotal);
    }

    [Fact]
    public void UpdateItemQuantityRejectsUnknownItem()
    {
        var quotation = NewQuotation();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.UpdateItemQuantity(QuotationItemId.New(), 1, 0m, 0, AdvisorId, Now));

        Assert.Equal("quotation.item.not_found", error.Code);
    }

    [Fact]
    public void RemoveItemDropsLineAndRecalculatesTotals()
    {
        var quotation = NewQuotation();
        var keep = QuotationItemId.New();
        var drop = QuotationItemId.New();
        quotation.AddItem(keep, Guid.CreateVersion7(), 1, 1000m, 0m, 0, AdvisorId, Now);
        quotation.AddItem(drop, Guid.CreateVersion7(), 1, 2000m, 0m, 0, AdvisorId, Now);

        quotation.RemoveItem(drop, AdvisorId, Now);

        var remaining = Assert.Single(quotation.Items);
        Assert.Equal(keep, remaining.Id);
        Assert.Equal(1000m, quotation.Subtotal);
    }

    [Fact]
    public void UpdateDetailsReplacesEditableFieldsAndTouchesVersion()
    {
        var quotation = NewQuotation(notes: "nota original");
        var validUntil = new DateOnly(2026, 9, 30);
        var parties = new QuotationParties(
            new QuotationPartyDetails { Name = "Nombre alterno" }, Shipping: null);

        quotation.UpdateDetails(validUntil, "Efectivo", null, parties, null, null, globalScaleFloor: null, AdvisorId, Now);

        Assert.Equal(validUntil, quotation.ValidUntil);
        Assert.Equal("Efectivo", quotation.PaymentMethod);
        Assert.Null(quotation.Notes);
        Assert.Equal("Nombre alterno", quotation.Billing?.Name);
        Assert.Null(quotation.Shipping);
        Assert.Equal(2, quotation.Version);
        Assert.Equal(AdvisorId, quotation.UpdatedBy);
    }

    // Una cotizacion nueva se entrega: recoger en tienda es una eleccion explicita de quien
    // cotiza, no el default.
    [Fact]
    public void CreateDefaultsToDeliveryInsteadOfStorePickup()
    {
        var quotation = NewQuotation();

        Assert.False(quotation.IsStorePickup);
    }

    // Recoger en tienda gana: si el request trae ademas una parte de entrega, no queda fila de
    // Shipping. La facturacion no tiene nada que ver con como se entrega.
    [Fact]
    public void CreateWithStorePickupDropsTheShippingPartyAndKeepsTheBilling()
    {
        var parties = new QuotationParties(
            new QuotationPartyDetails { Name = "Sede administrativa" },
            new QuotationPartyDetails { Name = "Bodega Fontibon", Address = "Zona Franca" },
            IsStorePickup: true);

        var quotation = NewQuotation(parties: parties);

        Assert.True(quotation.IsStorePickup);
        Assert.Null(quotation.Shipping);
        Assert.Equal("Sede administrativa", quotation.Billing?.Name);
    }

    [Fact]
    public void UpdateDetailsToStorePickupClearsAnExistingShippingParty()
    {
        var quotation = NewQuotation(parties: new QuotationParties(
            Billing: null, new QuotationPartyDetails { Name = "Bodega Fontibon" }));

        quotation.UpdateDetails(
            ValidUntil, "Efectivo", null,
            new QuotationParties(
                Billing: null,
                new QuotationPartyDetails { Name = "Bodega Fontibon" },
                IsStorePickup: true),
            null, null, globalScaleFloor: null, AdvisorId, Now);

        Assert.True(quotation.IsStorePickup);
        Assert.Null(quotation.Shipping);
        Assert.Empty(quotation.Parties);
    }

    // UpdateDetails reemplaza el recurso entero: un PATCH que no dice "recoger en tienda" vuelve
    // a la entrega, igual que una parte que no viene vuelve a los datos del cliente.
    [Fact]
    public void UpdateDetailsWithoutStorePickupReturnsToDelivery()
    {
        var quotation = NewQuotation(parties: QuotationParties.Empty with { IsStorePickup = true });

        quotation.UpdateDetails(
            ValidUntil, "Efectivo", null, QuotationParties.Empty, null, null, globalScaleFloor: null, AdvisorId, Now);

        Assert.False(quotation.IsStorePickup);
    }

    // Cambiar el cliente borra las partes porque copiaron al cliente viejo. Recoger en tienda no
    // copia nada del cliente: es como se entrega, y sigue valiendo para el nuevo.
    [Fact]
    public void ChangeClientKeepsStorePickup()
    {
        var quotation = NewQuotation(parties: QuotationParties.Empty with { IsStorePickup = true });

        quotation.ChangeClient(Guid.CreateVersion7(), false, false, AdvisorId, Now);

        Assert.True(quotation.IsStorePickup);
        Assert.Null(quotation.Shipping);
    }

    // Una cotizacion nueva factura a los datos del cliente: consumidor final es una eleccion
    // explicita de quien cotiza.
    [Fact]
    public void CreateDefaultsToBillingTheCustomer()
    {
        var quotation = NewQuotation();

        Assert.False(quotation.BillsToFinalConsumer);
    }

    // Consumidor final apaga la retencion y el excedente de IVA del cliente. Los snapshots del
    // cliente quedan intactos: son los que vuelven al desmarcar.
    [Fact]
    public void BillingToTheFinalConsumerChargesVatAndDropsRetention()
    {
        var quotation = NewQuotation(
            parties: QuotationParties.Empty with { BillsToFinalConsumer = true },
            customerWithRetention: true,
            customerVatSurplus: true);

        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 119_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);

        Assert.True(quotation.BillsToFinalConsumer);
        // 119_000 con el IVA del 19% adentro -> base 100_000 + IVA 19_000, que ahora se cobra.
        Assert.Equal(100_000m, quotation.Subtotal);
        Assert.Equal(19_000m, quotation.TaxAmount);
        Assert.Equal(19m, quotation.TaxPercentage);
        Assert.Equal(119_000m, quotation.Total);
        Assert.Equal(0m, quotation.RetentionAmount);
        Assert.Equal(119_000m, quotation.NetTotal);
        Assert.True(quotation.CustomerWithRetention);
        Assert.True(quotation.CustomerVatSurplus);
        Assert.False(quotation.AppliesRetention);
        Assert.False(quotation.AppliesVatSurplus);
    }

    // El camino real es el PATCH del encabezado: marcarlo ahi tambien recalcula.
    [Fact]
    public void UpdateDetailsToTheFinalConsumerRecalculatesTheTotals()
    {
        var quotation = NewQuotation(customerWithRetention: true, customerVatSurplus: true);
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 119_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);
        Assert.Equal(2_500m, quotation.RetentionAmount);

        quotation.UpdateDetails(
            ValidUntil, "Efectivo", null,
            QuotationParties.Empty with { BillsToFinalConsumer = true },
            null, null, globalScaleFloor: null, AdvisorId, Now);

        Assert.True(quotation.BillsToFinalConsumer);
        Assert.Equal(19_000m, quotation.TaxAmount);
        Assert.Equal(0m, quotation.RetentionAmount);
        Assert.Equal(119_000m, quotation.NetTotal);
        Assert.Equal(3, quotation.Version);
    }

    // Desmarcar no vuelve a consultar al cliente: los snapshots nunca se pisaron, asi que la
    // retencion y el excedente vuelven solos.
    [Fact]
    public void UnmarkingTheFinalConsumerRestoresTheCustomerRetentionAndVatSurplus()
    {
        var quotation = NewQuotation(
            parties: QuotationParties.Empty with { BillsToFinalConsumer = true },
            customerWithRetention: true,
            customerVatSurplus: true);
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 119_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);

        quotation.UpdateDetails(
            ValidUntil, "Efectivo", null, QuotationParties.Empty, null, null, globalScaleFloor: null, AdvisorId, Now);

        Assert.False(quotation.BillsToFinalConsumer);
        Assert.Equal(0m, quotation.TaxAmount);
        Assert.Equal(100_000m, quotation.Total);
        Assert.Equal(2_500m, quotation.RetentionAmount);
        Assert.Equal(97_500m, quotation.NetTotal);
    }

    // Con datos propios de facturacion, quien responde retencion/excedente de IVA es esa
    // respuesta y no el snapshot del cliente -- el cliente ya no es a quien se le factura.
    [Fact]
    public void AnOwnBillingPartyAnsweringYesAppliesRetentionAndVatSurplusRegardlessOfTheCustomer()
    {
        var ownBilling = new QuotationPartyDetails { Name = "Sede administrativa" };
        var quotation = NewQuotation(
            parties: new QuotationParties(
                ownBilling, Shipping: null,
                BillingWithRetention: true, BillingVatSurplus: true),
            customerWithRetention: false,
            customerVatSurplus: false);
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 119_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);

        Assert.True(quotation.AppliesRetention);
        Assert.True(quotation.AppliesVatSurplus);
        Assert.Equal(2_500m, quotation.RetentionAmount);
        Assert.Equal(0m, quotation.TaxAmount);
    }

    // Misma idea al reves: el cliente aplica retencion/excedente, pero la parte propia contesto
    // que no -- lo que dice la parte gana porque a ella se le factura.
    [Fact]
    public void AnOwnBillingPartyAnsweringNoIgnoresACustomerThatDoesApply()
    {
        var ownBilling = new QuotationPartyDetails { Name = "Sede administrativa" };
        var quotation = NewQuotation(
            parties: new QuotationParties(
                ownBilling, Shipping: null,
                BillingWithRetention: false, BillingVatSurplus: false),
            customerWithRetention: true,
            customerVatSurplus: true);
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 119_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);

        Assert.False(quotation.AppliesRetention);
        Assert.False(quotation.AppliesVatSurplus);
        Assert.Equal(0m, quotation.RetentionAmount);
        Assert.Equal(19_000m, quotation.TaxAmount);
    }

    // Sin fila de facturacion propia, la pregunta ni se hace: manda el cliente, como antes.
    [Fact]
    public void WithoutAnOwnBillingPartyTheCustomerSnapshotStillDrivesTaxes()
    {
        var quotation = NewQuotation(customerWithRetention: true, customerVatSurplus: false);
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 119_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);

        Assert.Null(quotation.PartyWithRetention);
        Assert.Null(quotation.PartyVatSurplus);
        Assert.True(quotation.AppliesRetention);
        Assert.False(quotation.AppliesVatSurplus);
    }

    // ChangeClient borra la fila de facturacion propia (US-2): la pregunta de retencion/excedente
    // que dependia de esa fila vuelve a "sin contestar" en vez de arrastrar la respuesta vieja.
    [Fact]
    public void ChangeClientResetsTheOwnBillingPartyTaxProfileAnswers()
    {
        var ownBilling = new QuotationPartyDetails { Name = "Sede administrativa" };
        var quotation = NewQuotation(
            parties: new QuotationParties(
                ownBilling, Shipping: null,
                BillingWithRetention: true, BillingVatSurplus: true));

        quotation.ChangeClient(
            Guid.CreateVersion7(), customerWithRetention: false, customerVatSurplus: false,
            AdvisorId, Now);

        Assert.Null(quotation.Billing);
        Assert.Null(quotation.PartyWithRetention);
        Assert.Null(quotation.PartyVatSurplus);
    }

    // Nombre y NIT de consumidor final son fijos: una parte propia al lado diria otro nombre para
    // la misma factura. Se rechaza en vez de elegir una en silencio.
    [Fact]
    public void CreateRejectsTheFinalConsumerWithABillingParty()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            NewQuotation(parties: new QuotationParties(
                new QuotationPartyDetails { Name = "Sede administrativa" },
                Shipping: null,
                BillsToFinalConsumer: true)));

        Assert.Equal("quotation.billing.final_consumer_conflict", error.Code);
    }

    [Fact]
    public void CreateRejectsTheFinalConsumerWithTheBusinessName()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            NewQuotation(parties: QuotationParties.Empty with
            {
                BillingUsesBusinessName = true,
                BillsToFinalConsumer = true,
            }));

        Assert.Equal("quotation.billing.final_consumer_conflict", error.Code);
    }

    // El rechazo llega antes de tocar nada: un PATCH que falla no deja la mitad del encabezado
    // aplicada.
    [Fact]
    public void UpdateDetailsRejectsTheFinalConsumerWithABillingPartyWithoutChangingAnything()
    {
        var quotation = NewQuotation(notes: "nota original");

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.UpdateDetails(
                ValidUntil, "Efectivo", "nota nueva",
                new QuotationParties(
                    new QuotationPartyDetails { Name = "Sede administrativa" },
                    Shipping: null,
                    BillsToFinalConsumer: true),
                null, null, globalScaleFloor: null, AdvisorId, Now));

        Assert.Equal("quotation.billing.final_consumer_conflict", error.Code);
        Assert.Equal("nota original", quotation.Notes);
        Assert.Null(quotation.Billing);
        Assert.False(quotation.BillsToFinalConsumer);
        Assert.Equal(1, quotation.Version);
    }

    [Fact]
    public void UpdateDetailsRejectsTheFinalConsumerWithTheBusinessName()
    {
        var quotation = NewQuotation();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.UpdateDetails(
                ValidUntil, "Efectivo", null,
                QuotationParties.Empty with
                {
                    BillingUsesBusinessName = true,
                    BillsToFinalConsumer = true,
                },
                null, null, globalScaleFloor: null, AdvisorId, Now));

        Assert.Equal("quotation.billing.final_consumer_conflict", error.Code);
    }

    // Un cliente nuevo arranca con la facturacion por defecto, igual que BillingUsesBusinessName:
    // la decision de facturar a consumidor final se tomo mirando al cliente anterior.
    [Fact]
    public void ChangeClientResetsTheFinalConsumer()
    {
        var quotation = NewQuotation(
            parties: QuotationParties.Empty with { BillsToFinalConsumer = true });
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 119_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);

        quotation.ChangeClient(
            Guid.CreateVersion7(), customerWithRetention: true, customerVatSurplus: false,
            AdvisorId, Now);

        Assert.False(quotation.BillsToFinalConsumer);
        Assert.Equal(2_500m, quotation.RetentionAmount);
    }

    // RefreshCustomerTaxProfile sigue actualizando los snapshots, pero con consumidor final la
    // retencion efectiva sigue en cero y el IVA se sigue cobrando.
    [Fact]
    public void RefreshCustomerTaxProfileKeepsTheFinalConsumerFreeOfRetention()
    {
        var quotation = NewQuotation(
            parties: QuotationParties.Empty with { BillsToFinalConsumer = true });
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 119_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);

        quotation.RefreshCustomerTaxProfile(customerWithRetention: true, customerVatSurplus: true);

        Assert.True(quotation.CustomerWithRetention);
        Assert.True(quotation.CustomerVatSurplus);
        Assert.Equal(0m, quotation.RetentionAmount);
        Assert.Equal(19_000m, quotation.TaxAmount);
    }

    [Fact]
    public void SendMarksAsSentAndStampsSentAt()
    {
        var quotation = NewQuotation();

        quotation.Send(AdvisorId, Now);

        Assert.Equal(QuotationStatus.Sent, quotation.Status);
        Assert.Equal(Now, quotation.SentAt);
        Assert.Equal(2, quotation.Version);
    }

    [Fact]
    public void SendRejectsAQuotationWithoutAValidityDate()
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", ClientId, AdvisorId,
            validUntil: null, null, null, QuotationParties.Empty, null, false, false, AdvisorId, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.Send(AdvisorId, Now));

        Assert.Equal("quotation.quotation.valid_until_required", error.Code);
        Assert.Equal(QuotationStatus.Draft, quotation.Status);
    }

    [Fact]
    // Reenviar una cotización que no cambió es un caso legítimo: al cliente se le puede haber
    // perdido el mensaje. El reenvío pisa SentAt en vez de conservar el del envío anterior. El
    // documento no se regenera si nada cambió -- eso lo decide `QuotationPdf.IsStaleFor`, fuera
    // de este agregado.
    public void SendResendsASentQuotationThatDidNotChange()
    {
        var quotation = NewQuotation();
        var later = Now.AddHours(3);
        quotation.Send(AdvisorId, Now);

        quotation.Send(AdvisorId, later);

        Assert.Equal(QuotationStatus.Sent, quotation.Status);
        Assert.Equal(later, quotation.SentAt);
        Assert.Equal(3, quotation.Version);
    }

    [Fact]
    // El botón sigue disponible después de enviar — es lo que hace posible el reenvío. Y un
    // reenvío no deja la cotización "con cambios": Send deja SentAt y UpdatedAt en el mismo
    // instante, igual que el primer envío.
    public void CanBeSentStaysTrueAfterSendingWithoutChanges()
    {
        var quotation = NewQuotation();

        quotation.Send(AdvisorId, Now);

        Assert.True(quotation.CanBeSent);
        Assert.False(quotation.HasChangesSinceSent);
    }

    [Theory]
    [InlineData(QuotationStatus.Voided)]
    [InlineData(QuotationStatus.Expired)]
    // Abrir el reenvío no abre las transiciones que ya estaban cerradas: una anulada o una
    // vencida no vuelven a Sent.
    public void SendStillRejectsAQuotationThatIsNotDraftOrSent(QuotationStatus status)
    {
        var quotation = NewQuotation();
        if (status == QuotationStatus.Voided)
        {
            quotation.Void(AdvisorId, Now);
        }
        else
        {
            quotation.Send(AdvisorId, Now);
            quotation.Expire(Now.AddDays(60));
        }

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.Send(AdvisorId, Now.AddDays(61)));

        Assert.Equal("quotation.quotation.not_draft", error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VoidWorksFromDraftOrSent(bool sendFirst)
    {
        var quotation = NewQuotation();
        if (sendFirst)
        {
            quotation.Send(AdvisorId, Now);
        }

        quotation.Void(AdvisorId, Now);

        Assert.Equal(QuotationStatus.Voided, quotation.Status);
    }

    [Fact]
    public void VoidRejectsAnAlreadyVoidedQuotation()
    {
        var quotation = NewQuotation();
        quotation.Void(AdvisorId, Now);

        var error = Assert.Throws<QuotationsDomainException>(() => quotation.Void(AdvisorId, Now));

        Assert.Equal("quotation.quotation.not_editable", error.Code);
    }

    // US-10: se puede editar en Draft y Sent, pero no despues de anulada -- US-11 dice
    // explicitamente que una cotizacion anulada "queda de solo lectura".
    [Fact]
    public void EditingAVoidedQuotationIsRejected()
    {
        var quotation = NewQuotation();
        quotation.Void(AdvisorId, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.AddItem(QuotationItemId.New(), Guid.CreateVersion7(), 1, 1000m, 0m, 0, AdvisorId, Now));
        Assert.Equal("quotation.quotation.not_editable", error.Code);

        var updateError = Assert.Throws<QuotationsDomainException>(() =>
            quotation.UpdateDetails(null, null, null, QuotationParties.Empty, null, null, globalScaleFloor: null, AdvisorId, Now));
        Assert.Equal("quotation.quotation.not_editable", updateError.Code);
    }

    [Fact]
    public void EditingASentQuotationIsAllowed()
    {
        var quotation = NewQuotation();
        quotation.Send(AdvisorId, Now);

        quotation.AddItem(QuotationItemId.New(), Guid.CreateVersion7(), 1, 1000m, 0m, 0, AdvisorId, Now);

        Assert.Single(quotation.Items);
    }

    [Fact]
    public void ExpireMovesASentQuotationToExpired()
    {
        var quotation = NewQuotation();
        quotation.Send(AdvisorId, Now);
        var updatedByBeforeExpiring = quotation.UpdatedBy;

        quotation.Expire(Now);

        Assert.Equal(QuotationStatus.Expired, quotation.Status);
        // Sin actor humano: UpdatedBy no cambia con el vencimiento automatico.
        Assert.Equal(updatedByBeforeExpiring, quotation.UpdatedBy);
    }

    [Fact]
    public void ExpireRejectsAQuotationThatIsNotSent()
    {
        var quotation = NewQuotation();

        var error = Assert.Throws<QuotationsDomainException>(() => quotation.Expire(Now));

        Assert.Equal("quotation.quotation.not_sent", error.Code);
    }

    [Fact]
    public void EditingAnExpiredQuotationIsRejected()
    {
        var quotation = NewQuotation();
        quotation.Send(AdvisorId, Now);
        quotation.Expire(Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.AddItem(QuotationItemId.New(), Guid.CreateVersion7(), 1, 1000m, 0m, 0, AdvisorId, Now));

        Assert.Equal("quotation.quotation.not_editable", error.Code);
    }

    // EnsureConvertibleToOrder sigue siendo solo el guard de precondicion, sin mutar nada: quien
    // pasa la cotizacion a Converted es ConvertToOrder, que lo llama primero. CanBeConvertedToOrder
    // y la pantalla dependen de que preguntar no cambie el estado.
    [Fact]
    public void EnsureConvertibleToOrderDoesNotThrowOrChangeStatusForASentQuotation()
    {
        // Con todo lo que el pedido hereda: productos, vigencia, forma de pago y cuenta de cobro.
        var quotation = NewQuotation(billingAccount: BillingAccount);
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 119_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);
        quotation.Send(AdvisorId, Now);
        var versionBeforeConverting = quotation.Version;

        quotation.EnsureConvertibleToOrder();

        Assert.Equal(QuotationStatus.Sent, quotation.Status);
        Assert.Equal(versionBeforeConverting, quotation.Version);
    }

    // A pedido (2026-09) ya no hace falta haberla enviado: un borrador con los cuatro datos que
    // el pedido hereda se convierte igual que uno ya enviado.
    [Fact]
    public void EnsureConvertibleToOrderDoesNotThrowForADraftWithEverythingAOrderNeeds()
    {
        var quotation = NewQuotation(billingAccount: BillingAccount);
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 119_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);

        quotation.EnsureConvertibleToOrder();

        Assert.Equal(QuotationStatus.Draft, quotation.Status);
        Assert.True(quotation.CanBeConvertedToOrder);
    }

    [Fact]
    public void EnsureConvertibleToOrderRejectsAVoidedQuotation()
    {
        var quotation = NewQuotation(billingAccount: BillingAccount);
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 119_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);
        quotation.Void(AdvisorId, Now);

        var error = Assert.Throws<QuotationsDomainException>(
            () => quotation.EnsureConvertibleToOrder());

        Assert.Equal("quotation.quotation.status_not_convertible", error.Code);
        Assert.False(quotation.CanBeConvertedToOrder);
    }

    [Fact]
    public void EnsureConvertibleToOrderRejectsAnExpiredQuotation()
    {
        var quotation = NewQuotation(billingAccount: BillingAccount);
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 119_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);
        quotation.Send(AdvisorId, Now);
        quotation.Expire(Now);

        var error = Assert.Throws<QuotationsDomainException>(
            () => quotation.EnsureConvertibleToOrder());

        Assert.Equal("quotation.quotation.status_not_convertible", error.Code);
    }

    // Convertir en pedido deja la cotización en Converted, venga de Draft o de Sent: es el mismo
    // hecho para el agregado, y lo que queda es un documento de sólo lectura con su pedido.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConvertToOrderMovesTheQuotationToConverted(bool sendFirst)
    {
        var quotation = NewQuotation(billingAccount: BillingAccount);
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 119_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);
        if (sendFirst)
        {
            quotation.Send(AdvisorId, Now);
        }

        var versionBeforeConverting = quotation.Version;
        var convertedBy = new MemberId(Guid.CreateVersion7());
        var convertedAt = Now.AddHours(2);

        quotation.ConvertToOrder(convertedBy, convertedAt);

        Assert.Equal(QuotationStatus.Converted, quotation.Status);
        Assert.Equal(convertedBy, quotation.UpdatedBy);
        Assert.Equal(convertedAt, quotation.UpdatedAt);
        Assert.Equal(versionBeforeConverting + 1, quotation.Version);
        Assert.False(quotation.CanBeConvertedToOrder);
        Assert.False(quotation.CanBeSent);
    }

    // Una ya convertida tampoco se convierte de nuevo: el estado lo corta antes de que el índice
    // único de Order.QuotationId tenga que hacerlo.
    [Theory]
    [InlineData(QuotationStatus.Voided)]
    [InlineData(QuotationStatus.Expired)]
    [InlineData(QuotationStatus.Converted)]
    public void ConvertToOrderRejectsAQuotationThatIsNotDraftOrSent(QuotationStatus status)
    {
        var quotation = ConvertibleSentQuotation();
        MoveTo(quotation, status);
        var versionBefore = quotation.Version;

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.ConvertToOrder(AdvisorId, Now.AddDays(61)));

        Assert.Equal("quotation.quotation.status_not_convertible", error.Code);
        Assert.Equal(status, quotation.Status);
        Assert.Equal(versionBefore, quotation.Version);
    }

    // ConvertToOrder no se salta ninguna precondición de EnsureConvertibleToOrder, y si una falla
    // la cotización se queda como estaba.
    [Fact]
    public void ConvertToOrderStillEnforcesThePreconditionsWithoutChangingTheStatus()
    {
        var quotation = NewQuotation(billingAccount: BillingAccount);
        quotation.Send(AdvisorId, Now);
        var versionBefore = quotation.Version;

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.ConvertToOrder(AdvisorId, Now.AddHours(1)));

        Assert.Equal("quotation.quotation.items_required", error.Code);
        Assert.Equal(QuotationStatus.Sent, quotation.Status);
        Assert.Equal(versionBefore, quotation.Version);
    }

    // US-10: "se bloquea una vez convertida a pedido". Converted queda de sólo lectura, igual que
    // Voided y Expired.
    [Fact]
    public void EditingAConvertedQuotationIsRejected()
    {
        var quotation = ConvertibleSentQuotation();
        quotation.ConvertToOrder(AdvisorId, Now.AddHours(1));

        var addError = Assert.Throws<QuotationsDomainException>(() =>
            quotation.AddItem(QuotationItemId.New(), Guid.CreateVersion7(), 1, 1000m, 0m, 0, AdvisorId, Now));
        Assert.Equal("quotation.quotation.not_editable", addError.Code);

        var updateError = Assert.Throws<QuotationsDomainException>(() =>
            quotation.UpdateDetails(null, null, null, QuotationParties.Empty, null, null, globalScaleFloor: null, AdvisorId, Now));
        Assert.Equal("quotation.quotation.not_editable", updateError.Code);
    }

    [Fact]
    public void VoidRejectsAConvertedQuotation()
    {
        var quotation = ConvertibleSentQuotation();
        quotation.ConvertToOrder(AdvisorId, Now.AddHours(1));

        var error = Assert.Throws<QuotationsDomainException>(() => quotation.Void(AdvisorId, Now));

        Assert.Equal("quotation.quotation.not_editable", error.Code);
        Assert.Equal(QuotationStatus.Converted, quotation.Status);
    }

    // EnsureSendable es lo que SendQuotationHandler llama antes de firmar el PDF y de hablar con
    // WhatsApp: una convertida tiene que caerse ahí, antes de cualquier efecto externo.
    [Fact]
    public void SendingAConvertedQuotationIsRejected()
    {
        var quotation = ConvertibleSentQuotation();
        quotation.ConvertToOrder(AdvisorId, Now.AddHours(1));

        var ensureError = Assert.Throws<QuotationsDomainException>(quotation.EnsureSendable);
        Assert.Equal("quotation.quotation.not_draft", ensureError.Code);

        var sendError = Assert.Throws<QuotationsDomainException>(() =>
            quotation.Send(AdvisorId, Now.AddHours(2)));
        Assert.Equal("quotation.quotation.not_draft", sendError.Code);
    }

    [Fact]
    public void ExpireRejectsAConvertedQuotation()
    {
        var quotation = ConvertibleSentQuotation();
        quotation.ConvertToOrder(AdvisorId, Now.AddHours(1));

        var error = Assert.Throws<QuotationsDomainException>(() => quotation.Expire(Now.AddDays(60)));

        Assert.Equal("quotation.quotation.not_sent", error.Code);
        Assert.Equal(QuotationStatus.Converted, quotation.Status);
    }

    // Mientras una convertida seguía en Sent, RefreshCustomerTaxProfile le recalculaba los
    // totales si el cliente cambiaba de perfil fiscal, aunque el pedido ya los hubiera heredado.
    // En Converted queda tal cual quedó, igual que una anulada o una vencida.
    [Fact]
    public void RefreshCustomerTaxProfileLeavesAConvertedQuotationAsItWas()
    {
        var quotation = ConvertibleSentQuotation();
        quotation.ConvertToOrder(AdvisorId, Now.AddHours(1));
        var totalBefore = quotation.Total;
        var taxBefore = quotation.TaxAmount;

        quotation.RefreshCustomerTaxProfile(customerWithRetention: true, customerVatSurplus: true);

        Assert.False(quotation.CustomerWithRetention);
        Assert.False(quotation.CustomerVatSurplus);
        Assert.Equal(totalBefore, quotation.Total);
        Assert.Equal(taxBefore, quotation.TaxAmount);
    }

    /// <summary>Lleva una cotización ya enviada al estado pedido por el camino real de cada
    /// transición, sin tocar el estado a mano.</summary>
    private static void MoveTo(Quotation quotation, QuotationStatus status)
    {
        switch (status)
        {
            case QuotationStatus.Voided:
                quotation.Void(AdvisorId, Now);
                break;
            case QuotationStatus.Expired:
                quotation.Expire(Now.AddDays(60));
                break;
            case QuotationStatus.Converted:
                quotation.ConvertToOrder(AdvisorId, Now.AddHours(1));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "No transition to that status.");
        }
    }

    // A pedido (2026-09), editar una enviada ya no la vuelve inconvertible: la vieja regla
    // exigia reenviarla antes de convertir para que el pedido heredara lo mismo que el cliente
    // vio, pero dejaba inconvertible a cualquier cotizacion que se corrigiera despues de
    // enviarse sin que nadie se lo pidiera. Sigue editable (US-10) y ahora tambien convertible
    // asi, sin reenviar.
    [Fact]
    public void CanBeConvertedToOrderStaysTrueAfterEditingASentQuotation()
    {
        var quotation = ConvertibleSentQuotation();
        Assert.True(quotation.CanBeConvertedToOrder);

        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 2, unitPrice: 50_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now.AddHours(1));

        Assert.True(quotation.HasChangesSinceSent);
        Assert.True(quotation.CanBeConvertedToOrder);
    }

    [Fact]
    public void EnsureConvertibleToOrderDoesNotThrowAfterEditingASentQuotation()
    {
        var quotation = ConvertibleSentQuotation();
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 2, unitPrice: 50_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now.AddHours(1));

        quotation.EnsureConvertibleToOrder();
    }

    [Fact]
    public void EnsureConvertibleToOrderRejectsAnIncompleteBillingParty()
    {
        var quotation = NewQuotation(
            billingAccount: BillingAccount,
            parties: new QuotationParties(
                new QuotationPartyDetails { Name = "Sede administrativa" }, Shipping: null));
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 119_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);

        var error = Assert.Throws<QuotationsDomainException>(
            () => quotation.EnsureConvertibleToOrder());

        Assert.Equal("quotation.billing.party_incomplete", error.Code);
        Assert.False(quotation.CanBeConvertedToOrder);
    }

    [Fact]
    public void EnsureConvertibleToOrderRejectsAnIncompleteShippingParty()
    {
        var quotation = NewQuotation(
            billingAccount: BillingAccount,
            parties: new QuotationParties(
                Billing: null, new QuotationPartyDetails { Name = "Bodega Fontibon" }));
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 119_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);

        var error = Assert.Throws<QuotationsDomainException>(
            () => quotation.EnsureConvertibleToOrder());

        Assert.Equal("quotation.shipping.party_incomplete", error.Code);
        Assert.False(quotation.CanBeConvertedToOrder);
    }

    // Los seis campos, no menos: es el mismo umbral que exige el editor antes de dejar mandar
    // la cotizacion (quote-party-fields.tsx), y un pedido no puede heredar una direccion a
    // medio llenar.
    [Fact]
    public void EnsureConvertibleToOrderAcceptsACompleteOwnParty()
    {
        var completeParty = new QuotationPartyDetails
        {
            Name = "Sede administrativa",
            Phone = "3105550134",
            Email = "compras@sede.co",
            Address = "Calle 10 # 45-12",
            DepartmentId = Guid.CreateVersion7(),
            CityId = Guid.CreateVersion7(),
        };
        var quotation = NewQuotation(
            billingAccount: BillingAccount,
            parties: new QuotationParties(
                completeParty, completeParty,
                BillingWithRetention: false, BillingVatSurplus: false));
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 119_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);

        quotation.EnsureConvertibleToOrder();

        Assert.True(quotation.CanBeConvertedToOrder);
    }

    // Con datos propios de facturacion nadie mas dice si hay retencion/excedente de IVA: null
    // es "todavia no se contesto", y eso deja el pedido bloqueado aunque la parte este completa.
    [Fact]
    public void EnsureConvertibleToOrderRejectsAnOwnBillingPartyWithoutATaxProfileAnswer()
    {
        var completeParty = new QuotationPartyDetails
        {
            Name = "Sede administrativa",
            Phone = "3105550134",
            Email = "compras@sede.co",
            Address = "Calle 10 # 45-12",
            DepartmentId = Guid.CreateVersion7(),
            CityId = Guid.CreateVersion7(),
        };
        var quotation = NewQuotation(
            billingAccount: BillingAccount,
            parties: new QuotationParties(completeParty, completeParty));
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 119_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);

        var exception = Assert.Throws<QuotationsDomainException>(quotation.EnsureConvertibleToOrder);

        Assert.Equal("quotation.billing.tax_profile_required", exception.Code);
        Assert.False(quotation.CanBeConvertedToOrder);
    }

    // A pedido (2026-09): "Editar" un pedido pendiente para sumarle productos que faltaron al
    // convertir, sin recrear el pedido entero. Sólo sumar — ver Order.RecalculatePaymentStatus
    // para la otra mitad de esta historia.
    [Fact]
    public void AddItemAfterConversionAddsALineToAConvertedQuotation()
    {
        var quotation = ConvertibleSentQuotation();
        quotation.ConvertToOrder(AdvisorId, Now);
        var totalBefore = quotation.Total;
        var productId = Guid.CreateVersion7();
        var later = Now.AddDays(1);

        quotation.AddItemAfterConversion(
            QuotationItemId.New(), productId, quantity: 2, unitPrice: 50_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, later);

        Assert.Equal(2, quotation.Items.Count);
        Assert.Contains(quotation.Items, item => item.ProductId == productId);
        Assert.True(quotation.Total > totalBefore);
        Assert.Equal(later, quotation.UpdatedAt);
        Assert.Equal(AdvisorId, quotation.UpdatedBy);
    }

    // Bug real: las conversiones de antes del 2026-09-13 no movían el estado de la cotización
    // (el campo se agregó ese día, sin backfill), así que hay pedidos Pending reales cuya
    // cotización quedó en Sent para siempre. Exigir Converted acá las dejaba sin poder
    // editarse — el caso de uso ya encontró el pedido pendiente antes de llamar, y eso alcanza.
    [Fact]
    public void AddItemAfterConversionAddsALineEvenWhenTheQuotationWasNeverBackfilledToConverted()
    {
        var quotation = ConvertibleSentQuotation();
        var productId = Guid.CreateVersion7();

        quotation.AddItemAfterConversion(
            QuotationItemId.New(), productId, quantity: 1, unitPrice: 50_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now.AddDays(1));

        Assert.Contains(quotation.Items, item => item.ProductId == productId);
    }

    // Mismo invariante que AddItem: dos líneas del mismo producto es la misma línea con la
    // cantidad partida.
    [Fact]
    public void AddItemAfterConversionRejectsADuplicateProduct()
    {
        var quotation = ConvertibleSentQuotation();
        var existingProductId = Assert.Single(quotation.Items).ProductId;
        quotation.ConvertToOrder(AdvisorId, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.AddItemAfterConversion(
                QuotationItemId.New(), existingProductId, 1, 1000m, 0m, 0,
                AdvisorId, Now.AddDays(1)));

        Assert.Equal("quotation.item.duplicate_product", error.Code);
    }

    // A pedido (2026-09-15): "Editar" un pedido pendiente también admite corregir la cantidad de
    // una línea que ya tenía — mismo endpoint que UpdateItemQuantity, sin EnsureEditable.
    [Fact]
    public void UpdateItemQuantityAfterConversionChangesTheQuantityOfAConvertedQuotation()
    {
        var quotation = ConvertibleSentQuotation();
        quotation.ConvertToOrder(AdvisorId, Now);
        var itemId = Assert.Single(quotation.Items).Id;
        var totalBefore = quotation.Total;
        var later = Now.AddDays(1);

        quotation.UpdateItemQuantityAfterConversion(
            itemId, quantity: 5, discountPercentage: 0m, taxPercentage: 19, AdvisorId, later);

        Assert.Equal(5, Assert.Single(quotation.Items).Quantity);
        Assert.True(quotation.Total > totalBefore);
        Assert.Equal(later, quotation.UpdatedAt);
    }

    [Fact]
    public void UpdateItemQuantityAfterConversionRejectsAnUnknownItem()
    {
        var quotation = ConvertibleSentQuotation();
        quotation.ConvertToOrder(AdvisorId, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.UpdateItemQuantityAfterConversion(
                QuotationItemId.New(), 1, 0m, 0, AdvisorId, Now.AddDays(1)));

        Assert.Equal("quotation.item.not_found", error.Code);
    }

    // A pedido (2026-09-15): quitar un producto que ya no correspondía, sin recrear el pedido.
    [Fact]
    public void RemoveItemAfterConversionRemovesALineWhenMoreThanOneRemains()
    {
        var quotation = ConvertibleSentQuotation();
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 50_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);
        quotation.ConvertToOrder(AdvisorId, Now);
        var itemToRemove = quotation.Items.First().Id;
        var later = Now.AddDays(1);

        quotation.RemoveItemAfterConversion(itemToRemove, AdvisorId, later);

        Assert.Single(quotation.Items);
        Assert.DoesNotContain(quotation.Items, item => item.Id == itemToRemove);
        Assert.Equal(later, quotation.UpdatedAt);
    }

    // Sin este chequeo, un pedido pendiente podía quedarse con una cotización en cero productos
    // y un total en cero, sin ninguna vuelta atrás (items_required sólo se valida al convertir).
    [Fact]
    public void RemoveItemAfterConversionRejectsRemovingTheLastRemainingItem()
    {
        var quotation = ConvertibleSentQuotation();
        quotation.ConvertToOrder(AdvisorId, Now);
        var itemId = Assert.Single(quotation.Items).Id;

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.RemoveItemAfterConversion(itemId, AdvisorId, Now.AddDays(1)));

        Assert.Equal("quotation.item.last_item_required", error.Code);
        Assert.Single(quotation.Items);
    }

    [Fact]
    public void RemoveItemAfterConversionRejectsAnUnknownItem()
    {
        var quotation = ConvertibleSentQuotation();
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 50_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);
        quotation.ConvertToOrder(AdvisorId, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.RemoveItemAfterConversion(QuotationItemId.New(), AdvisorId, Now.AddDays(1)));

        Assert.Equal("quotation.item.not_found", error.Code);
    }

    /// <summary>Enviada y con los cuatro datos que el pedido hereda: productos, vigencia, forma
    /// de pago y cuenta de cobro.</summary>
    private static Quotation ConvertibleSentQuotation()
    {
        var quotation = NewQuotation(billingAccount: BillingAccount);
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 119_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);
        quotation.Send(AdvisorId, Now);
        return quotation;
    }

    // ---- Recalculo global de descuentos (2026-09-21) ----
    //
    // La agrupacion y la compuerta de compra minima se resuelven mirando la cotizacion entera, no
    // linea por linea, asi que la aplicacion necesita poder reescribir el descuento de todas las
    // lineas de una sola vez. No es una edicion: no toca la version ni el autor, y por eso
    // tampoco pasa por EnsureEditable.
    [Fact]
    public void ApplyGroupDiscountsRewritesEveryItemDiscountAndRecalculates()
    {
        var quotation = NewQuotation();
        var first = QuotationItemId.New();
        var second = QuotationItemId.New();

        quotation.AddItem(first, Guid.NewGuid(), 3m, 10_000m, 0m, 19, AdvisorId, Now);
        quotation.AddItem(second, Guid.NewGuid(), 3m, 20_000m, 0m, 19, AdvisorId, Now);

        var versionBefore = quotation.Version;

        quotation.ApplyGroupDiscounts(
            new Dictionary<QuotationItemId, QuotationItemDiscount>
            {
                [first] = new(5m, QuotationDiscountOrigin.Group),
                [second] = new(5m, QuotationDiscountOrigin.Group)
            },
            Now);

        Assert.Equal(5m, quotation.Items.Single(item => item.Id == first).DiscountPercentage);
        Assert.Equal(5m, quotation.Items.Single(item => item.Id == second).DiscountPercentage);

        // 3 x 10.000 + 3 x 20.000 = 90.000, menos 5% = 85.500, IVA adentro.
        Assert.Equal(85_500m, quotation.Total);
        Assert.Equal(4_500m, quotation.DiscountAmount);
        Assert.Equal(versionBefore, quotation.Version);
    }

    // La compuerta de compra minima devuelve descuentos a 0, asi que el camino de vuelta tiene que
    // existir y rehacer los totales igual de bien.
    [Fact]
    public void ApplyGroupDiscountsCanTakeADiscountBackToZero()
    {
        var quotation = NewQuotation();
        var item = QuotationItemId.New();

        quotation.AddItem(item, Guid.NewGuid(), 3m, 10_000m, 5m, 19, AdvisorId, Now);
        Assert.Equal(1_500m, quotation.DiscountAmount);

        quotation.ApplyGroupDiscounts(
            new Dictionary<QuotationItemId, QuotationItemDiscount>
            {
                [item] = new(0m, QuotationDiscountOrigin.Own)
            },
            Now);

        Assert.Equal(0m, quotation.Items.Single().DiscountPercentage);
        Assert.Equal(0m, quotation.DiscountAmount);
        Assert.Equal(30_000m, quotation.Total);
    }

    // Una linea que no viene en el diccionario se queda como estaba: el recalculo manda lo que
    // resolvio, y lo que no menciona no se toca.
    [Fact]
    public void ApplyGroupDiscountsLeavesUnmentionedItemsAlone()
    {
        var quotation = NewQuotation();
        var mentioned = QuotationItemId.New();
        var untouched = QuotationItemId.New();

        quotation.AddItem(mentioned, Guid.NewGuid(), 3m, 10_000m, 0m, 19, AdvisorId, Now);
        quotation.AddItem(untouched, Guid.NewGuid(), 3m, 10_000m, 7m, 19, AdvisorId, Now);

        quotation.ApplyGroupDiscounts(
            new Dictionary<QuotationItemId, QuotationItemDiscount>
            {
                [mentioned] = new(5m, QuotationDiscountOrigin.Group)
            },
            Now);

        Assert.Equal(5m, quotation.Items.Single(item => item.Id == mentioned).DiscountPercentage);
        Assert.Equal(7m, quotation.Items.Single(item => item.Id == untouched).DiscountPercentage);
    }
}
