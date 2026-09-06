using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

public sealed class QuotationTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly ValidUntil = new(2026, 9, 30);

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

        quotation.UpdateDetails(validUntil, "Efectivo", null, parties, null, null, AdvisorId, Now);

        Assert.Equal(validUntil, quotation.ValidUntil);
        Assert.Equal("Efectivo", quotation.PaymentMethod);
        Assert.Null(quotation.Notes);
        Assert.Equal("Nombre alterno", quotation.Billing?.Name);
        Assert.Null(quotation.Shipping);
        Assert.Equal(2, quotation.Version);
        Assert.Equal(AdvisorId, quotation.UpdatedBy);
    }

    [Fact]
    public void SendMarksAsSentAndStampsThePdfFileAndSentAt()
    {
        var quotation = NewQuotation();
        var pdfFileId = Guid.CreateVersion7();

        quotation.Send(pdfFileId, AdvisorId, Now);

        Assert.Equal(QuotationStatus.Sent, quotation.Status);
        Assert.Equal(pdfFileId, quotation.PdfFileId);
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
            quotation.Send(Guid.CreateVersion7(), AdvisorId, Now));

        Assert.Equal("quotation.quotation.valid_until_required", error.Code);
        Assert.Equal(QuotationStatus.Draft, quotation.Status);
    }

    [Fact]
    public void SendRejectsAQuotationThatIsNotDraft()
    {
        var quotation = NewQuotation();
        quotation.Send(Guid.CreateVersion7(), AdvisorId, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.Send(Guid.CreateVersion7(), AdvisorId, Now));

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
            quotation.Send(Guid.CreateVersion7(), AdvisorId, Now);
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
            quotation.UpdateDetails(null, null, null, QuotationParties.Empty, null, null, AdvisorId, Now));
        Assert.Equal("quotation.quotation.not_editable", updateError.Code);
    }

    [Fact]
    public void EditingASentQuotationIsAllowed()
    {
        var quotation = NewQuotation();
        quotation.Send(Guid.CreateVersion7(), AdvisorId, Now);

        quotation.AddItem(QuotationItemId.New(), Guid.CreateVersion7(), 1, 1000m, 0m, 0, AdvisorId, Now);

        Assert.Single(quotation.Items);
    }

    [Fact]
    public void ExpireMovesASentQuotationToExpired()
    {
        var quotation = NewQuotation();
        quotation.Send(Guid.CreateVersion7(), AdvisorId, Now);
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
        quotation.Send(Guid.CreateVersion7(), AdvisorId, Now);
        quotation.Expire(Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.AddItem(QuotationItemId.New(), Guid.CreateVersion7(), 1, 1000m, 0m, 0, AdvisorId, Now));

        Assert.Equal("quotation.quotation.not_editable", error.Code);
    }

    // No hay un estado "aprobada": convertir a venta deja la cotizacion en Sent (ver
    // QuotationStatus) -- EnsureConvertibleToSale es solo el guard de precondicion que
    // ConvertQuotationToSaleHandler llama antes de crear la Sale, no muta nada.
    [Fact]
    public void EnsureConvertibleToSaleDoesNotThrowOrChangeStatusForASentQuotation()
    {
        var quotation = NewQuotation();
        quotation.Send(Guid.CreateVersion7(), AdvisorId, Now);
        var versionBeforeConverting = quotation.Version;

        quotation.EnsureConvertibleToSale();

        Assert.Equal(QuotationStatus.Sent, quotation.Status);
        Assert.Equal(versionBeforeConverting, quotation.Version);
    }

    [Fact]
    public void EnsureConvertibleToSaleRejectsAQuotationThatIsNotSent()
    {
        var quotation = NewQuotation();

        var error = Assert.Throws<QuotationsDomainException>(
            () => quotation.EnsureConvertibleToSale());

        Assert.Equal("quotation.quotation.not_sent", error.Code);
    }
}
