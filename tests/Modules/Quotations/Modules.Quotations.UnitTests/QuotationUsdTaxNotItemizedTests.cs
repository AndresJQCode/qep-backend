using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Hotfix 2026-10-14: en USD el IVA no se discrimina. El precio del producto se cobra tal cual,
/// como si trajera el IVA adentro, pero ni la línea ni el encabezado lo separan: el impuesto
/// queda en cero y la base es el precio completo. Antes se le extraía el IVA igual que en COP y
/// la cotización en dólares mostraba un subtotal recortado y un IVA que no se factura.
/// </summary>
public sealed class QuotationUsdTaxNotItemizedTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    // 100 × 24,63 con 5% son 2.339,85: se cobran enteros, sin IVA aparte y sin base recortada.
    [Fact]
    public void InUsdTheLineKeepsThePriceAndItemizesNoTax()
    {
        var quotation = NewQuotation("USD");
        AddItem(quotation, quantity: 100m, unitPrice: 24.63m, discountPercentage: 5m, taxPercentage: 19);

        var item = quotation.Items.Single();

        Assert.Equal(0, item.TaxPercentage);
        Assert.Equal(0m, item.TaxAmount);
        Assert.Equal(2_339.85m, item.Subtotal);
        Assert.Equal(123.15m, item.DiscountAmount);
        Assert.Equal(23.40m, item.DiscountedUnitPrice);
        Assert.Equal(23.40m, item.DiscountedUnitPriceWithoutTax);
        Assert.Equal(24.63m, item.UnitPriceWithoutTax);
    }

    [Fact]
    public void InUsdTheHeaderHasNoTaxAndTheTotalIsTheSubtotal()
    {
        var quotation = NewQuotation("USD");
        AddItem(quotation, quantity: 100m, unitPrice: 24.63m, discountPercentage: 5m, taxPercentage: 19);
        AddItem(quotation, quantity: 2m, unitPrice: 10m, discountPercentage: 0m, taxPercentage: 5);

        Assert.Equal(2_359.85m, quotation.Subtotal);
        Assert.Equal(0m, quotation.TaxAmount);
        Assert.Equal(0m, quotation.TaxPercentage);
        Assert.Equal(2_359.85m, quotation.Total);
    }

    // En COP nada cambia: el IVA se sigue extrayendo del precio.
    [Fact]
    public void InCopTheTaxIsStillItemized()
    {
        var quotation = NewQuotation("COP");
        AddItem(quotation, quantity: 100m, unitPrice: 35_000m, discountPercentage: 0m, taxPercentage: 19);

        var item = quotation.Items.Single();

        Assert.Equal(19, item.TaxPercentage);
        Assert.Equal(558_823.53m, item.TaxAmount);
        Assert.Equal(2_941_176.47m, item.Subtotal);
        Assert.Equal(558_823.53m, quotation.TaxAmount);
        Assert.Equal(3_500_000m, quotation.Total);
    }

    // Pasar de COP a USD deja de discriminar el IVA de las líneas que ya existían.
    [Fact]
    public void RepricingIntoUsdDropsTheItemizedTax()
    {
        var quotation = NewQuotation("COP");
        var productId = Guid.CreateVersion7();
        quotation.AddItem(QuotationItemId.New(), productId, 100m, 24_563m, 0m, 19, AdvisorId, Now);
        Assert.NotEqual(0m, quotation.TaxAmount);

        Reprice(quotation, "USD", productId, new QuotationItemPricing(24.63m, 5m, 19));

        Assert.Equal(QuotationCurrency.Usd, quotation.Currency);
        Assert.Equal(0m, quotation.TaxAmount);
        Assert.Equal(0, quotation.Items.Single().TaxPercentage);
        Assert.Equal(2_339.85m, quotation.Total);
    }

    // Y volver a COP lo discrimina otra vez con la tasa del catálogo, no con el cero guardado.
    [Fact]
    public void RepricingIntoCopItemizesTheTaxAgain()
    {
        var quotation = NewQuotation("USD");
        var productId = Guid.CreateVersion7();
        quotation.AddItem(QuotationItemId.New(), productId, 100m, 24.63m, 5m, 19, AdvisorId, Now);
        Assert.Equal(0m, quotation.TaxAmount);

        Reprice(quotation, "COP", productId, new QuotationItemPricing(24_563m, 5m, 19));

        Assert.Equal(QuotationCurrency.Cop, quotation.Currency);
        Assert.Equal(19, quotation.Items.Single().TaxPercentage);
        Assert.Equal(372_575.63m, quotation.TaxAmount);
        Assert.Equal(1_960_924.37m, quotation.Subtotal);
        Assert.Equal(2_333_500m, quotation.Total);
    }

    // Sin base sin IVA no hay sobre qué retener: en USD la retención en la fuente no se resta
    // aunque el cliente la practique (owner, 2026-10-14).
    [Fact]
    public void InUsdTheRetentionIsNotSubtractedEvenForACustomerWithRetention()
    {
        var quotation = NewQuotation("USD", customerWithRetention: true);
        AddItem(quotation, quantity: 100m, unitPrice: 24.63m, discountPercentage: 5m, taxPercentage: 19);

        Assert.Equal(0m, quotation.RetentionAmount);
        Assert.Equal(2_339.85m, quotation.NetTotal);
        Assert.Equal(quotation.Total, quotation.NetTotal);
    }

    // Y al volver a COP la retención vuelve: 2,5% de la base sin IVA.
    [Fact]
    public void RepricingIntoCopSubtractsTheRetentionAgain()
    {
        var quotation = NewQuotation("USD", customerWithRetention: true);
        var productId = Guid.CreateVersion7();
        quotation.AddItem(QuotationItemId.New(), productId, 1m, 119m, 0m, 19, AdvisorId, Now);
        Assert.Equal(0m, quotation.RetentionAmount);

        Reprice(quotation, "COP", productId, new QuotationItemPricing(119_000m, 0m, 19));

        Assert.Equal(2_500m, quotation.RetentionAmount);
        Assert.Equal(116_500m, quotation.NetTotal);
    }

    private static void Reprice(
        Quotation quotation, string currency, Guid productId, QuotationItemPricing pricing) =>
        quotation.UpdateDetails(
            new DateOnly(2026, 10, 31),
            "Transferencia bancaria",
            notes: null,
            QuotationParties.Empty,
            BillingAccount(currency),
            new Dictionary<Guid, QuotationItemPricing> { [productId] = pricing },
            isRetail: false,
            globalScaleFloor: null,
            AdvisorId,
            Now);

    private static Quotation NewQuotation(string currency, bool customerWithRetention = false) =>
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
            customerWithRetention,
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
            Guid.CreateVersion7(),
            quantity,
            unitPrice,
            discountPercentage,
            taxPercentage,
            AdvisorId,
            Now);
}
