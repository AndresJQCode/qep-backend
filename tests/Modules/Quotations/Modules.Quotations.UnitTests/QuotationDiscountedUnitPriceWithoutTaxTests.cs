using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El precio unitario neto: con el descuento aplicado y sin IVA. Es lo que el Excel de pedidos pone
/// en "Valor Unit" (2026-10-03) y sale de <c>Subtotal / Quantity</c>: el subtotal ya es la base sin
/// IVA de la línea después del descuento, así que no hay que repetir la cuenta acá.
/// </summary>
public sealed class QuotationDiscountedUnitPriceWithoutTaxTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    // 3 × 119.000 = 357.000; el 10% son 35.700 y se cobran 321.300, IVA del 19% adentro. Ese IVA
    // es 321.300 × 19 / 119 = 51.300, la base 270.000 y por unidad 90.000 — 100.000 sin IVA menos
    // el 10%.
    [Fact]
    public void AppliesTheDiscountAndRemovesTheVat()
    {
        var quotation = NewQuotation();
        AddItem(quotation, quantity: 3m, unitPrice: 119_000m, discountPercentage: 10m, taxPercentage: 19);

        var item = quotation.Items.Single();

        Assert.Equal(90_000m, item.DiscountedUnitPriceWithoutTax);
        Assert.Equal(item.Subtotal, item.Quantity * item.DiscountedUnitPriceWithoutTax);
    }

    // Sin tasa no hay IVA que quitar: queda sólo el descuento. 4 × 45.000 = 180.000, menos 18.000.
    [Fact]
    public void WithoutATaxRateItIsTheDiscountedUnitPrice()
    {
        var quotation = NewQuotation();
        AddItem(quotation, quantity: 4m, unitPrice: 45_000m, discountPercentage: 10m, taxPercentage: 0);

        var item = quotation.Items.Single();

        Assert.Equal(40_500m, item.DiscountedUnitPriceWithoutTax);
        Assert.Equal(item.DiscountedUnitPrice, item.DiscountedUnitPriceWithoutTax);
    }

    // 12 × 35.900 = 430.800; el 15% son 64.620 y se cobran 366.180. IVA: 366.180 × 19 / 119 =
    // 58.465,714… → 58.465,71; base 307.714,29; por unidad 25.642,8575 → 25.642,86.
    [Fact]
    public void RoundsToTwoDecimalsLikeEveryOtherAmountOfTheLine()
    {
        var quotation = NewQuotation();
        AddItem(quotation, quantity: 12m, unitPrice: 35_900m, discountPercentage: 15m, taxPercentage: 19);

        var item = quotation.Items.Single();

        Assert.Equal(25_642.86m, item.DiscountedUnitPriceWithoutTax);
    }

    // El piso global que elige el asesor queda en el descuento de la línea, así que entra igual que
    // el de la escala propia: 2 × 119.000 con 20% cobra 190.400, base 160.000, 80.000 por unidad.
    [Fact]
    public void IncludesADiscountThatCameFromTheGlobalFloor()
    {
        var quotation = NewQuotation();
        AddItem(quotation, quantity: 2m, unitPrice: 119_000m, discountPercentage: 0m, taxPercentage: 19);
        var item = quotation.Items.Single();

        quotation.ApplyGroupDiscounts(
            new Dictionary<QuotationItemId, QuotationItemDiscount>
            {
                [item.Id] = new(20m, QuotationDiscountOrigin.GlobalFloor),
            },
            Now);

        Assert.Equal(QuotationDiscountOrigin.GlobalFloor, item.DiscountOrigin);
        Assert.Equal(80_000m, item.DiscountedUnitPriceWithoutTax);
    }

    private static Quotation NewQuotation() =>
        Quotation.Create(
            QuotationId.New(),
            TenantId,
            "QUO-2026-0001",
            ClientId,
            AdvisorId,
            new DateOnly(2026, 10, 31),
            paymentMethod: "Transferencia bancaria",
            notes: null,
            QuotationParties.Empty,
            new QuotationBillingAccount
            {
                CompanyId = Guid.CreateVersion7(),
                BankName = "Bancolombia",
                AccountNumber = "12345678",
                Currency = "COP"
            },
            QuotationCurrency.Cop,
            customerWithRetention: false,
            customerVatSurplus: false,
            AdvisorId,
            Now);

    private static void AddItem(
        Quotation quotation, decimal quantity, decimal unitPrice, decimal discountPercentage, int taxPercentage) =>
        quotation.AddItem(
            QuotationItemId.New(),
            Guid.CreateVersion7(),
            quantity,
            unitPrice,
            discountPercentage,
            taxPercentage,
            AdvisorId,
            Now);
}
