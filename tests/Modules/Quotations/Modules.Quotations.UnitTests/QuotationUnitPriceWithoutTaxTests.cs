using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El precio unitario sin el IVA que trae adentro. <c>UnitPrice</c> se carga con IVA incluido
/// (<c>QuotationItem.Apply</c>), así que el Excel de pedidos lo necesita derivado y no como un
/// campo aparte: "Valor Unit sin IVA" (2026-09-24). Misma familia de fórmula que <c>TaxAmount</c>
/// (× tasa / (100 + tasa)), redondeado a dos decimales como el resto de la línea.
/// </summary>
public sealed class QuotationUnitPriceWithoutTaxTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    // 119.000 con IVA del 19%: la base es 100.000 y el IVA contenido por unidad, 19.000. El
    // descuento no entra: es el precio bruto de la línea, no el que se cobra.
    [Fact]
    public void RemovesTheVatContainedInTheUnitPrice()
    {
        var quotation = NewQuotation();
        AddItem(quotation, quantity: 3m, unitPrice: 119_000m, discountPercentage: 10m, taxPercentage: 19);

        var item = quotation.Items.Single();

        Assert.Equal(100_000m, item.UnitPriceWithoutTax);
        Assert.Equal(19_000m, item.UnitPrice - item.UnitPriceWithoutTax);
    }

    // Sin tasa no hay nada que quitar: la columna repite el precio unitario en vez de quedar
    // vacía, igual que DiscountedUnitPrice sin descuento.
    [Fact]
    public void WithoutATaxRateItEqualsTheUnitPrice()
    {
        var quotation = NewQuotation();
        AddItem(quotation, quantity: 1m, unitPrice: 45_000m, discountPercentage: 0m, taxPercentage: 0);

        var item = quotation.Items.Single();

        Assert.Equal(45_000m, item.UnitPriceWithoutTax);
    }

    // 35.900 × 100 / 119 = 30.168,0672…: se redondea a dos decimales, como todo importe de la línea.
    [Fact]
    public void RoundsToTwoDecimalsLikeEveryOtherAmountOfTheLine()
    {
        var quotation = NewQuotation();
        AddItem(quotation, quantity: 12m, unitPrice: 35_900m, discountPercentage: 15m, taxPercentage: 19);

        var item = quotation.Items.Single();

        Assert.Equal(30_168.07m, item.UnitPriceWithoutTax);
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
