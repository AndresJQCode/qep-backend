using System.Globalization;
using BuildingBlocks.Domain.Pricing;
using Modules.Pos.Domain;
using Modules.Quotations.Domain;
using PosMemberId = Modules.Pos.Domain.MemberId;
using QuotationsMemberId = Modules.Quotations.Domain.MemberId;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Cotización, pedido y POS no se ven entre sí, pero los tres cobran una línea con IVA incluido con la misma
/// aritmética (spec 2026-10-27 §1, <see cref="VatIncludedLine"/>). Este proyecto ve los tres módulos: si uno
/// deja de usar la fórmula compartida, la misma línea cuesta distinto según dónde se venda y esta prueba se rompe.
/// </summary>
public sealed class PriceRoundingParityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly QuotationsMemberId Advisor = new(Guid.CreateVersion7());
    private static readonly PosMemberId Cashier = new(Guid.CreateVersion7());

    private static decimal D(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("10", "13.19", "15", 2, PriceRoundingMode.Up)]
    [InlineData("1.25", "35000", "33.33", 0, PriceRoundingMode.Nearest)]
    public void QuotationOrderAndPosGiveTheSameLine(
        string quantityText, string priceText, string discountText, int decimals, PriceRoundingMode mode)
    {
        var quantity = D(quantityText);
        var price = D(priceText);
        var discount = D(discountText);
        var rule = new PriceRounding(decimals, mode);
        var currency = decimals == 2 ? "USD" : "COP";
        var variant = Guid.CreateVersion7();

        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", Guid.CreateVersion7(), Advisor, null, null, null,
            QuotationParties.Empty, null, currency, false, false, Advisor, Now);
        quotation.UsePriceRounding(rule, Now);
        quotation.AddItem(QuotationItemId.New(), variant, variant, quantity, price, discount, 0, Advisor, Now);
        var quotationItem = Assert.Single(quotation.Items);

        var order = Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0001",
            new OrderHeader(Guid.CreateVersion7(), Advisor, currency, null, IsRetail: false, null, false, false),
            QuotationParties.Empty,
            new QuotationBillingAccount
            {
                CompanyId = Guid.CreateVersion7(), BankName = "Banco", AccountNumber = "123", Currency = currency,
            },
            ShippingDetails.None,
            [new OrderLineInput(variant, variant, quantity, price, discount, 0)],
            OrderPaymentStatus.PaymentPending, null, Advisor, [], Now);
        order.UsePriceRounding(rule, Now);
        var orderItem = Assert.Single(order.Items);

        var session = CashSession.Open(
            CashSessionId.New(), TenantId, Cashier, "Cajero",
            new PosCompanySnapshot(Guid.CreateVersion7(), "Empresa", "900123456-1", "Calle 1", "6045551234"),
            currency, 0m, Now);
        var sale = PosSale.Create(
            PosSaleId.New(), new string('a', 64), session,
            [new PosSaleLineInput(variant, variant, "SKU", "Producto", quantity, price, discount, 0)],
            [new PosPaymentInput(PosPaymentMethod.Cash, null, 1_000_000m, null)], Now, rounding: rule);
        var posLine = Assert.Single(sale.Lines);

        Assert.Equal(quotationItem.UnitPrice, orderItem.UnitPrice);
        Assert.Equal(quotationItem.UnitPrice, posLine.UnitPrice);
        Assert.Equal(quotationItem.DiscountAmount, orderItem.DiscountAmount);
        Assert.Equal(quotationItem.DiscountAmount, posLine.DiscountAmount);
        Assert.Equal(quotationItem.Subtotal + quotationItem.TaxAmount, orderItem.Subtotal + orderItem.TaxAmount);
        Assert.Equal(quotationItem.Subtotal + quotationItem.TaxAmount, posLine.LineTotal);
    }
}
