using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.UnitTests;

// Spec 2026-11-23: al crear y al calcular el pedido manual, los regalos viajan en su lista, se precian a lista con el 100 %
// de descuento (sin escalas) y van después de las líneas pagadas.
public sealed class OrderDraftBuilderGiftTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly Guid CompanyId = Guid.CreateVersion7();
    private static readonly Guid VariantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 15, 0, 0, TimeSpan.Zero);

    private static OrderDraftInput NewInput(IReadOnlyList<OrderItemAddition>? gifts) =>
        new(
            TenantId, ClientId, new MemberId(Guid.CreateVersion7()), null, null, false, null, null,
            new QuotationBillingAccountRequest(CompanyId, "Bancolombia", "7542", "COP"),
            [new OrderItemAddition(VariantId, 10m)],
            [], AllowIncompleteCustomer: false, Gifts: gifts);

    // La variante cuesta 1.000 y tiene una escala del 10 % desde 10 unidades.
    private static OrderDraftBuilder NewBuilder()
    {
        var settings = new FixedQuotationSettingsStore();
        return new OrderDraftBuilder(
            new StubQuotationCustomerLookup(new QuotationCustomerRef(
                ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
                "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false)),
            new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>
            {
                [CompanyId] = new(
                    CompanyId, "Mi empresa", "800111222", "NIT", "CO", true, null, null,
                    [new QuotationCompanyAccountRef("Bancolombia", "7542", "COP")])
            }),
            new StubQuotationProductPricingLookup(new QuotationVariantPricingRef(
                VariantId, VariantId, TenantId, "Aceite", "ACE", true,
                new Dictionary<string, decimal> { ["COP"] = 1_000m },
                [new QuotationPriceScaleRef(10, 100, 10m, QuotationPriceScaleRestriction.Multiple, 1, [])], 19)),
            new StubQuotationProductLookup(new Dictionary<Guid, QuotationVariantRef>()),
            settings,
            new FixedTenantPriceRounding(),
            new ShippingResolver(new StubCarrierRepository(), new StubQuotationTaxRateLookup(null), settings),
            new FixedTenantDefaultCurrency(),
            new StubTenantDirectory
            {
                Regional = new(
                    "America/Bogota", "dd/MM/yyyy", "COP", TenantNumberFormats.CommaDecimal, "CO",
                    PersonTypeRequirement.Never)
            },
            new StubQuotationWithholdingLookup(),
            new FixedTenantClock(Now),
            new StubQuotationGeographyLookup(new Dictionary<Guid, string>()));
    }

    [Fact]
    public async Task PreviewPricesAGiftAtListPriceWithFullDiscount()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var withoutGifts = await NewBuilder().PreviewAsync(NewInput(null), Now, cancellationToken);

        var order = await NewBuilder().PreviewAsync(
            NewInput([new OrderItemAddition(VariantId, 12m)]), Now, cancellationToken);

        var gift = Assert.Single(order.Items, item => item.IsGift);
        Assert.Equal(1_000m, gift.UnitPrice);
        Assert.Equal(100m, gift.DiscountPercentage);
        Assert.Equal(0m, gift.Subtotal);
        Assert.Equal(12m, gift.Quantity);
        var paid = Assert.Single(order.Items, item => !item.IsGift);
        Assert.Equal(10m, paid.DiscountPercentage);
        Assert.Equal(withoutGifts.Total, order.Total);
        // Primero las pagadas, después los regalos.
        Assert.Equal([false, true], order.Items.OrderBy(item => item.Position).Select(item => item.IsGift));
    }

    [Fact]
    public async Task BuildAsyncKeepsTheGift()
    {
        var order = await NewBuilder().BuildAsync(
            NewInput([new OrderItemAddition(VariantId, 2m)]), "PED-2026-0001", Now, TestContext.Current.CancellationToken);

        var gift = Assert.Single(order.Items, item => item.IsGift);
        Assert.Equal(2m, gift.Quantity);
        Assert.Equal(100m, gift.DiscountPercentage);
        Assert.Equal(2, order.Items.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoGiftsWhenTheListIsNullOrEmpty(bool empty)
    {
        IReadOnlyList<OrderItemAddition>? gifts = empty ? [] : null;

        var order = await NewBuilder().PreviewAsync(NewInput(gifts), Now, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(order.Items, item => item.IsGift);
    }
}
