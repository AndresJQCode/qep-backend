using BuildingBlocks.Domain.Pricing;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El gemelo de <see cref="QuotationPriceRoundingTests"/> sobre el pedido: guarda la regla con la que se precia y
/// la cambia sin que sea una edición (spec 2026-10-27 §4).
/// </summary>
public sealed class OrderPriceRoundingTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly PriceRounding UsdUp = new(2, PriceRoundingMode.Up);

    [Fact]
    public void UsingUsdUpRecomputesEveryLine()
    {
        var order = NewUsdOrder();
        // Antes de la regla: 13,19 × 0,85 = 11,2115 → 11,21 al más cercano.
        Assert.Equal(112.10m, order.Items.Single().Subtotal);

        order.UsePriceRounding(UsdUp, Now.AddMinutes(5));

        var item = order.Items.Single();
        Assert.Equal(112.20m, item.Subtotal);
        Assert.Equal(19.70m, item.DiscountAmount);
        Assert.Equal(112.20m, order.ProductsTotal);
        Assert.Equal(UsdUp, order.PriceRounding);
    }

    [Fact]
    public void UsingTheSameRuleIsANoOp()
    {
        var order = NewUsdOrder();
        order.UsePriceRounding(UsdUp, Now);
        var version = order.Version;
        var updatedAt = order.UpdatedAt;
        var line = order.Items.Single();
        var amounts = (line.Subtotal, line.DiscountAmount, line.UpdatedAt);

        order.UsePriceRounding(UsdUp, Now.AddHours(1));

        Assert.Equal((version, updatedAt), (order.Version, order.UpdatedAt));
        Assert.Equal(amounts, (line.Subtotal, line.DiscountAmount, line.UpdatedAt));
    }

    [Fact]
    public void ChangingTheRuleIsNotAnEdit()
    {
        var order = NewUsdOrder();
        var (version, updatedAt) = (order.Version, order.UpdatedAt);

        order.UsePriceRounding(UsdUp, Now.AddHours(1));

        Assert.Equal((version, updatedAt), (order.Version, order.UpdatedAt));
    }

    private static Order NewUsdOrder()
    {
        var variantId = Guid.CreateVersion7();
        return Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0001",
            new OrderHeader(Guid.CreateVersion7(), AdvisorId, "USD", null, IsRetail: false, null, false, false),
            QuotationParties.Empty,
            new QuotationBillingAccount
            {
                CompanyId = Guid.CreateVersion7(),
                BankName = "Bancolombia",
                AccountNumber = "12345678",
                Currency = "USD",
            },
            ShippingDetails.None,
            [new OrderLineInput(variantId, variantId, 10m, 13.19m, 15m, 19)],
            OrderPaymentStatus.PaymentPending, null, AdvisorId, [], Now);
    }
}
