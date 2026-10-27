using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Hotfix 2026-10-14: en COP el precio unitario con IVA, ya con el descuento, es de pesos enteros.
///
/// El programa de facturación masiva arma lo que le manda a Siigo así: redondea a pesos
/// "Valor Unit" × (1 + IVA) y multiplica por la cantidad. Lo que consigna el cliente es el
/// "Total facturado" de QEP. Con un descuento que dejaba el unitario en 23.334,85, QEP cobraba
/// 2.333.485 por 100 unidades y la carga llegaba a Siigo con 23.335 × 100 = 2.333.500: la factura no
/// cuadraba contra las consignaciones. Con el unitario entero las dos cuentas son la misma.
/// </summary>
public sealed class QuotationWholePesoUnitPriceTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    // 24.563 con 5% son 23.334,85 por unidad: se cobra 23.335.
    [Fact]
    public void ADiscountThatLeavesCentsIsRoundedToWholePesosPerUnit()
    {
        var quotation = NewQuotation("COP");
        AddItem(quotation, quantity: 100m, unitPrice: 24_563m, discountPercentage: 5m, taxPercentage: 19);

        var item = quotation.Items.Single();

        Assert.Equal(23_335m, item.DiscountedUnitPrice);
        Assert.Equal(2_333_500m, quotation.Total);
    }

    // El caso real: kit de keratina 7703 a 35.000 con IVA, 100 unidades en la escala de 33,33%.
    // 35.000 × 0,6667 = 23.334,50 por unidad, que se cobra 23.335. La base por unidad queda en
    // 19.609,24 —lo que el Excel pone en "Valor Unit"— y por 1,19 vuelve a dar 23.335.
    [Fact]
    public void TheKeratinKitAtTheThirtyThreePercentScaleChargesWholePesos()
    {
        var quotation = NewQuotation("COP");
        AddItem(quotation, quantity: 100m, unitPrice: 35_000m, discountPercentage: 33.33m, taxPercentage: 19);

        var item = quotation.Items.Single();

        Assert.Equal(23_335m, item.DiscountedUnitPrice);
        Assert.Equal(19_609.24m, item.DiscountedUnitPriceWithoutTax);
        Assert.Equal(1_166_500m, item.DiscountAmount);
        Assert.Equal(2_333_500m, quotation.Total);
    }

    // 1.001 con 50% son 500,5 exactos: lejos del cero, 501 — no 500 como el redondeo bancario.
    [Fact]
    public void AnExactHalfPesoGoesUp()
    {
        var quotation = NewQuotation("COP");
        AddItem(quotation, quantity: 2m, unitPrice: 1_001m, discountPercentage: 50m, taxPercentage: 19);

        Assert.Equal(501m, quotation.Items.Single().DiscountedUnitPrice);
        Assert.Equal(1_002m, quotation.Total);
    }

    // Un precio de lista con centavos también se cobra entero, y sin descuento negativo: redondear
    // sólo el neto haría que 23.335 × 100 superara al bruto de 23.334,50 × 100.
    [Fact]
    public void AListPriceWithCentsIsRoundedWithoutANegativeDiscount()
    {
        var quotation = NewQuotation("COP");
        AddItem(quotation, quantity: 100m, unitPrice: 23_334.50m, discountPercentage: 0m, taxPercentage: 19);

        var item = quotation.Items.Single();

        Assert.Equal(23_335m, item.DiscountedUnitPrice);
        Assert.Equal(0m, item.DiscountAmount);
        Assert.Equal(2_333_500m, quotation.Total);
    }

    // El descuento del grupo o del piso global entra por el mismo cálculo que el de la escala.
    [Fact]
    public void AGroupDiscountIsAlsoRoundedToWholePesos()
    {
        var quotation = NewQuotation("COP");
        AddItem(quotation, quantity: 100m, unitPrice: 24_563m, discountPercentage: 0m, taxPercentage: 19);
        var item = quotation.Items.Single();

        quotation.ApplyGroupDiscounts(
            new Dictionary<QuotationItemId, QuotationItemDiscount>
            {
                [item.Id] = new(5m, QuotationDiscountOrigin.Group),
            },
            Now);

        Assert.Equal(23_335m, item.DiscountedUnitPrice);
        Assert.Equal(2_333_500m, quotation.Total);
    }

    // En dólares el unitario se cobra en centavos (spec 2026-10-27): 24,63 × 0,95 = 23,3985 → 23,40,
    // y 100 × 23,40 = 2.340,00 (antes 2.339,85 con el unitario sin redondear).
    [Fact]
    public void InUsdTheUnitIsChargedInCents()
    {
        var quotation = NewQuotation("USD");
        AddItem(quotation, quantity: 100m, unitPrice: 24.63m, discountPercentage: 5m, taxPercentage: 19);

        Assert.Equal(2_340.00m, quotation.Total);
    }

    // Pasar de USD a COP vuelve a calcular las líneas con la moneda nueva, no con la vieja.
    [Fact]
    public void RepricingIntoCopRoundsWithTheNewCurrency()
    {
        var quotation = NewQuotation("USD");
        var productId = Guid.CreateVersion7();
        quotation.AddItem(QuotationItemId.New(), productId, productId, 100m, 6m, 0m, 19, AdvisorId, Now);

        quotation.UpdateDetails(
            new DateOnly(2026, 10, 31),
            "Transferencia bancaria",
            notes: null,
            QuotationParties.Empty,
            BillingAccount("COP"),
            new Dictionary<Guid, QuotationItemPricing> { [productId] = new(24_563m, 5m, 19) },
            isRetail: false,
            globalScaleFloor: null,
            AdvisorId,
            Now);

        Assert.Equal("COP", quotation.Currency);
        Assert.Equal(2_333_500m, quotation.Total);
    }

    // La regla de la carga masiva, tal cual la hace el programa que le manda a Siigo —redondeo
    // bancario de double, IVA fijo del 19%—, sumada línea por línea, da el Total facturado.
    [Theory]
    [InlineData(35_000, 33.33, 100)]
    [InlineData(24_563, 5, 100)]
    [InlineData(24_562, 5, 7)]
    [InlineData(35_900, 15, 12)]
    [InlineData(23_334.50, 0, 100)]
    [InlineData(19_990, 7.5, 3)]
    [InlineData(1_001, 50, 2)]
    public void TheBulkInvoicingRuleAddsUpToTheTotal(double unitPrice, double discountPercentage, int quantity)
    {
        var quotation = NewQuotation("COP");
        AddItem(quotation, quantity, (decimal)unitPrice, (decimal)discountPercentage, taxPercentage: 19);
        AddItem(quotation, 3m, 12_345m, (decimal)discountPercentage, taxPercentage: 19);

        var bulkTotal = quotation.Items.Sum(item =>
            (decimal)Math.Round((double)(item.DiscountedUnitPriceWithoutTax * 1.19m)) * item.Quantity);

        Assert.Equal(quotation.Total, bulkTotal);
    }

    private static Quotation NewQuotation(string currency) =>
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
            BillingAccount(currency),
            currency,
            customerWithRetention: false,
            customerVatSurplus: false,
            AdvisorId,
            Now);

    private static QuotationBillingAccount BillingAccount(string currency) =>
        new()
        {
            CompanyId = Guid.CreateVersion7(),
            BankName = "Bancolombia",
            AccountNumber = "12345678",
            Currency = currency
        };

    private static void AddItem(
        Quotation quotation, decimal quantity, decimal unitPrice, decimal discountPercentage, int taxPercentage) =>
        quotation.AddItem(
            QuotationItemId.New(),
            Guid.CreateVersion7(), Guid.CreateVersion7(),
            quantity,
            unitPrice,
            discountPercentage,
            taxPercentage,
            AdvisorId,
            Now);
}
