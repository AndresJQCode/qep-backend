using BuildingBlocks.Domain.Pricing;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// La regla de redondeo con la que se precia un documento (spec 2026-10-27 §4): la cotización la
/// guarda y el pedido la hereda, para que cambiar la regla del tenant después no mueva lo que el
/// cliente ya recibió. Un documento sin regla guardada se lee con la de siempre.
/// </summary>
public sealed class QuotationPriceRoundingTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly PriceRounding UsdUp = new(2, PriceRoundingMode.Up);

    [Fact]
    public void UsingUsdUpRecomputesEveryLine()
    {
        var quotation = NewQuotation("USD");
        AddItem(quotation, quantity: 10m, unitPrice: 13.19m, discountPercentage: 15m);
        // Antes de la regla: 13,19 × 0,85 = 11,2115 → 11,21 al más cercano.
        Assert.Equal(112.10m, quotation.Items.Single().Subtotal);

        quotation.UsePriceRounding(UsdUp, Now.AddMinutes(5));

        var item = quotation.Items.Single();
        Assert.Equal(112.20m, item.Subtotal);
        Assert.Equal(19.70m, item.DiscountAmount);
        Assert.Equal(112.20m, quotation.Total);
    }

    [Fact]
    public void UsingTheSameRuleIsANoOp()
    {
        var quotation = NewQuotation("USD");
        AddItem(quotation, quantity: 10m, unitPrice: 13.19m, discountPercentage: 15m);
        quotation.UsePriceRounding(UsdUp, Now);
        var version = quotation.Version;
        var updatedAt = quotation.UpdatedAt;
        var line = quotation.Items.Single();
        var amounts = (line.Subtotal, line.DiscountAmount, line.UpdatedAt);

        quotation.UsePriceRounding(UsdUp, Now.AddHours(1));

        Assert.Equal((version, updatedAt), (quotation.Version, quotation.UpdatedAt));
        Assert.Equal(amounts, (line.Subtotal, line.DiscountAmount, line.UpdatedAt));
    }

    [Fact]
    public void UsingARuleIsNotAnEdit()
    {
        var quotation = NewQuotation("USD");
        AddItem(quotation, quantity: 10m, unitPrice: 13.19m, discountPercentage: 15m);
        var version = quotation.Version;
        var updatedAt = quotation.UpdatedAt;
        var updatedBy = quotation.UpdatedBy;

        quotation.UsePriceRounding(UsdUp, Now.AddHours(1));

        Assert.Equal((version, updatedAt, updatedBy), (quotation.Version, quotation.UpdatedAt, quotation.UpdatedBy));
    }

    [Fact]
    public void ANewLineUsesTheStoredRule()
    {
        var quotation = NewQuotation("USD");
        quotation.UsePriceRounding(UsdUp, Now);

        AddItem(quotation, quantity: 1m, unitPrice: 13.19m, discountPercentage: 15m);

        Assert.Equal(11.22m, quotation.Items.Single().DiscountedUnitPriceUnder(quotation.PriceRounding));
    }

    [Fact]
    public void AnOrderFromAQuotationInheritsItsRule()
    {
        var quotation = NewQuotation("USD");
        AddItem(quotation, quantity: 10m, unitPrice: 13.19m, discountPercentage: 15m);
        quotation.UsePriceRounding(UsdUp, Now);
        quotation.ConvertToOrder(AdvisorId, Now, new Dictionary<Guid, QuotationProductSnapshot>
        {
            [quotation.Items.Single().VariantId] = new("ABC", "Camiseta"),
        });

        var order = Order.FromQuotation(
            OrderId.New(), TenantId, "PED-2026-0001", quotation, OrderPaymentStatus.PaymentPending, null,
            AdvisorId, [], Now);

        Assert.Equal(UsdUp, order.PriceRounding);
        var (quoted, ordered) = (quotation.Items.Single(), order.Items.Single());
        Assert.Equal(
            (quoted.UnitPrice, quoted.Subtotal, quoted.DiscountAmount),
            (ordered.UnitPrice, ordered.Subtotal, ordered.DiscountAmount));
        Assert.Equal(quotation.Total, order.Total);
        Assert.Equal(112.20m, ordered.Subtotal);
    }

    [Fact]
    public void ALegacyQuotationReadsTheDefaultRule()
    {
        Assert.Equal(new PriceRounding(2, PriceRoundingMode.Nearest), NewQuotation("USD").PriceRounding);
        Assert.Equal(new PriceRounding(0, PriceRoundingMode.Nearest), NewQuotation("COP").PriceRounding);
    }

    [Fact]
    public void ChargedFreightOutsidePesosFollowsTheRule()
    {
        var freight = FreightAmounts.For(
            FreightChargeMode.Charged, 12.50m, 0, "USD", new PriceRounding(0, PriceRoundingMode.Up));

        Assert.Equal(new FreightAmounts(13m, 0m), freight);
    }

    private static Quotation NewQuotation(string currency) =>
        Quotation.Create(
            QuotationId.New(),
            TenantId,
            "QUO-2026-0001",
            ClientId,
            AdvisorId,
            new DateOnly(2026, 11, 30),
            paymentMethod: "Transferencia bancaria",
            notes: null,
            QuotationParties.Empty,
            new QuotationBillingAccount
            {
                CompanyId = Guid.CreateVersion7(),
                BankName = "Bancolombia",
                AccountNumber = "12345678",
                Currency = currency
            },
            currency,
            customerWithRetention: false,
            customerVatSurplus: false,
            AdvisorId,
            Now);

    private static void AddItem(
        Quotation quotation, decimal quantity, decimal unitPrice, decimal discountPercentage) =>
        quotation.AddItem(
            QuotationItemId.New(),
            Guid.CreateVersion7(), Guid.CreateVersion7(),
            quantity,
            unitPrice,
            discountPercentage,
            taxPercentage: 19,
            AdvisorId,
            Now);
}
