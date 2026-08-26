using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

public sealed class QuotationTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    private static Quotation NewQuotation(
        string? notes = null, QuotationOverrides? overrides = null) =>
        Quotation.Create(
            QuotationId.New(),
            TenantId,
            "QUO-2026-0001",
            ClientId,
            AdvisorId,
            validUntil: null,
            paymentMethod: "Transferencia bancaria",
            notes,
            overrides ?? QuotationOverrides.Empty,
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
            null, null, null, QuotationOverrides.Empty, AdvisorId, Now);

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
                null, null, null, QuotationOverrides.Empty, AdvisorId, Now));

        Assert.Equal("quotation.quotation.number_required", error.Code);
    }

    [Fact]
    public void CreateRejectsQuotationNumberOverTwentyCharacters()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            Quotation.Create(
                QuotationId.New(), TenantId, new string('a', 21), ClientId, AdvisorId,
                null, null, null, QuotationOverrides.Empty, AdvisorId, Now));

        Assert.Equal("quotation.quotation.number_too_long", error.Code);
    }

    [Fact]
    public void CreateRejectsEmptyClientId()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            Quotation.Create(
                QuotationId.New(), TenantId, "QUO-2026-0001", Guid.Empty, AdvisorId,
                null, null, null, QuotationOverrides.Empty, AdvisorId, Now));

        Assert.Equal("quotation.quotation.client_required", error.Code);
    }

    // Escala de ejemplo del propio documento: 10-19 unidades, 5% de descuento.
    [Fact]
    public void AddItemComputesDiscountSubtotalAndTaxFromResolvedPercentages()
    {
        var quotation = NewQuotation();
        var productId = Guid.CreateVersion7();

        quotation.AddItem(
            QuotationItemId.New(), productId, quantity: 10, unitPrice: 100_000m,
            discountPercentage: 5m, taxPercentage: 19, AdvisorId, Now);

        var item = Assert.Single(quotation.Items);
        Assert.Equal(productId, item.ProductId);
        Assert.Equal(10m, item.Quantity);
        Assert.Equal(100_000m, item.UnitPrice);
        Assert.Equal(5m, item.DiscountPercentage);
        // gross = 10 * 100_000 = 1_000_000; discount = 5% = 50_000; subtotal = 950_000
        Assert.Equal(50_000m, item.DiscountAmount);
        Assert.Equal(950_000m, item.Subtotal);
        // tax = 19% of 950_000 = 180_500
        Assert.Equal(19, item.TaxPercentage);
        Assert.Equal(180_500m, item.TaxAmount);
        Assert.Equal(1, item.Position);
    }

    [Fact]
    public void AddItemRecalculatesHeaderTotals()
    {
        var quotation = NewQuotation();

        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 10, unitPrice: 100_000m,
            discountPercentage: 5m, taxPercentage: 19, AdvisorId, Now);

        // subtotal = 950_000; tax = 19% of 950_000 = 180_500; total = 1_130_500
        Assert.Equal(950_000m, quotation.Subtotal);
        Assert.Equal(50_000m, quotation.DiscountAmount);
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
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 100_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 100_000m,
            discountPercentage: 0m, taxPercentage: 0, AdvisorId, Now);

        // línea 1: 19% de 100_000 = 19_000; línea 2: 0% de 100_000 = 0; suma = 19_000
        Assert.Equal(200_000m, quotation.Subtotal);
        Assert.Equal(19_000m, quotation.TaxAmount);
        // tasa efectiva: 19_000 / 200_000 * 100 = 9.5
        Assert.Equal(9.5m, quotation.TaxPercentage);
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
        var overrides = new QuotationOverrides { BillingName = "Nombre alterno" };

        quotation.UpdateDetails(validUntil, "Efectivo", null, overrides, AdvisorId, Now);

        Assert.Equal(validUntil, quotation.ValidUntil);
        Assert.Equal("Efectivo", quotation.PaymentMethod);
        Assert.Null(quotation.Notes);
        Assert.Equal("Nombre alterno", quotation.BillingNameOverride);
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
            quotation.UpdateDetails(null, null, null, QuotationOverrides.Empty, AdvisorId, Now));
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

    [Fact]
    public void ApproveMovesASentQuotationToApproved()
    {
        var quotation = NewQuotation();
        quotation.Send(Guid.CreateVersion7(), AdvisorId, Now);

        quotation.Approve(AdvisorId, Now);

        Assert.Equal(QuotationStatus.Approved, quotation.Status);
        Assert.Equal(AdvisorId, quotation.UpdatedBy);
    }

    [Fact]
    public void ApproveRejectsAQuotationThatIsNotSent()
    {
        var quotation = NewQuotation();

        var error = Assert.Throws<QuotationsDomainException>(() => quotation.Approve(AdvisorId, Now));

        Assert.Equal("quotation.quotation.not_sent", error.Code);
    }

    [Fact]
    public void EditingAnApprovedQuotationIsRejected()
    {
        var quotation = NewQuotation();
        quotation.Send(Guid.CreateVersion7(), AdvisorId, Now);
        quotation.Approve(AdvisorId, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.AddItem(QuotationItemId.New(), Guid.CreateVersion7(), 1, 1000m, 0m, 0, AdvisorId, Now));

        Assert.Equal("quotation.quotation.not_editable", error.Code);
    }
}
