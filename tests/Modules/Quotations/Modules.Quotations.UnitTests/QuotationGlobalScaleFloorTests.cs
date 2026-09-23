using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El piso de escala global sobre el agregado: que se guarde, que sea una edicion y que la
/// cotizacion ya convertida solo lo acepte por la puerta del pedido.
/// </summary>
public sealed class QuotationGlobalScaleFloorTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly ValidUntil = new(2026, 10, 31);

    private static readonly QuotationBillingAccount BillingAccount = new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "12345678",
        Currency = "COP",
    };

    private static Quotation NewQuotation(QuotationBillingAccount? billingAccount = null) =>
        Quotation.Create(
            QuotationId.New(),
            TenantId,
            "QUO-2026-0001",
            ClientId,
            AdvisorId,
            ValidUntil,
            paymentMethod: "Transferencia bancaria",
            notes: null,
            QuotationParties.Empty,
            billingAccount,
            customerWithRetention: false,
            customerVatSurplus: false,
            AdvisorId,
            Now);

    private static Quotation ConvertedQuotation()
    {
        var quotation = NewQuotation(BillingAccount);
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 1, unitPrice: 119_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);
        quotation.ConvertToOrder(AdvisorId, Now.AddHours(2));

        return quotation;
    }

    [Fact]
    public void SettingTheGlobalScaleFloorIsAnEditAndBumpsTheVersion()
    {
        var quotation = NewQuotation();
        var versionBefore = quotation.Version;
        var editor = new MemberId(Guid.CreateVersion7());
        var editedAt = Now.AddHours(1);

        quotation.SetGlobalScaleFloor(1000, editor, editedAt);

        Assert.Equal(1000, quotation.GlobalScaleFloor);
        Assert.Equal(versionBefore + 1, quotation.Version);
        Assert.Equal(editor, quotation.UpdatedBy);
        Assert.Equal(editedAt, quotation.UpdatedAt);
    }

    [Fact]
    public void ANewQuotationHasNoGlobalScaleFloor()
    {
        Assert.Null(NewQuotation().GlobalScaleFloor);
    }

    [Fact]
    public void ClearingTheGlobalScaleFloorLeavesItNull()
    {
        var quotation = NewQuotation();
        quotation.SetGlobalScaleFloor(1000, AdvisorId, Now.AddHours(1));

        quotation.SetGlobalScaleFloor(null, AdvisorId, Now.AddHours(2));

        Assert.Null(quotation.GlobalScaleFloor);
    }

    // Una escala nunca arranca por debajo de 1 (invariante de Catalog), asi que un piso de cero o
    // negativo no puede coincidir con ninguna: se corta en el dominio y no llega al recalculo.
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void AFloorBelowOneIsRejected(int floor)
    {
        var quotation = NewQuotation();

        var error = Assert.Throws<QuotationsDomainException>(
            () => quotation.SetGlobalScaleFloor(floor, AdvisorId, Now.AddHours(1)));

        Assert.Equal("quotation.global_scale.floor_invalid", error.Code);
    }

    // Una cotizacion convertida no se edita por la puerta normal: para eso esta el mutador propio
    // del pedido, igual que AddItemAfterConversion.
    [Fact]
    public void AConvertedQuotationRejectsTheOrdinarySetter()
    {
        var quotation = ConvertedQuotation();

        var error = Assert.Throws<QuotationsDomainException>(
            () => quotation.SetGlobalScaleFloor(1000, AdvisorId, Now.AddHours(3)));

        Assert.Equal("quotation.quotation.not_editable", error.Code);
    }

    [Fact]
    public void AConvertedQuotationAcceptsTheAfterConversionSetter()
    {
        var quotation = ConvertedQuotation();

        quotation.SetGlobalScaleFloorAfterConversion(1000, AdvisorId, Now.AddHours(3));

        Assert.Equal(1000, quotation.GlobalScaleFloor);
    }

    // El recalculo baja descuento y origen juntos: aplicar uno sin el otro deja la linea diciendo
    // que su 12% se lo dio el piso global cuando la compuerta de compra minima ya se lo quito.
    [Fact]
    public void ApplyGroupDiscountsCarriesTheOriginIntoTheLine()
    {
        var quotation = NewQuotation();
        var itemId = QuotationItemId.New();
        quotation.AddItem(
            itemId, Guid.CreateVersion7(), quantity: 3, unitPrice: 100_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);

        quotation.ApplyGroupDiscounts(
            new Dictionary<QuotationItemId, QuotationItemDiscount>
            {
                [itemId] = new(12m, QuotationDiscountOrigin.GlobalFloor)
            },
            Now.AddHours(1));

        var item = quotation.Items.Single();
        Assert.Equal(12m, item.DiscountPercentage);
        Assert.Equal(QuotationDiscountOrigin.GlobalFloor, item.DiscountOrigin);
    }

    [Fact]
    public void ALineStartsWithItsDiscountOriginOnItsOwn()
    {
        var quotation = NewQuotation();
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity: 3, unitPrice: 100_000m,
            discountPercentage: 5m, taxPercentage: 19, AdvisorId, Now);

        Assert.Equal(QuotationDiscountOrigin.Own, quotation.Items.Single().DiscountOrigin);
    }
}
