using System.Text.Json;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Las escalas que cada línea de la respuesta trae para que la pantalla dibuje la tabla de
/// descuentos del producto. Viajan ordenadas por <c>fromUnit</c> ascendente: la tabla es un
/// rango continuo, y el orden en que Catalog las devuelva es un detalle de su almacenamiento —
/// dejarlo pasar obliga a cada consumidor (pantalla y PDF) a reordenarlas por su cuenta.
/// </summary>
public sealed class QuotationItemPriceScalesTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly Guid ProductId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task TheResponseOrdersEachItemsPriceScalesByFromUnit()
    {
        var quotation = NewQuotation();
        quotation.AddItem(
            QuotationItemId.New(),
            ProductId,
            quantity: 12m,
            unitPrice: 35_900m,
            discountPercentage: 15m,
            taxPercentage: 19,
            AdvisorId,
            Now);

        // Desordenadas a propósito, y no sólo invertidas: un `OrderBy` ausente pasaría igual si
        // el caso de prueba ya viniera casi ordenado.
        var response = await ComposeAsync(quotation, Scale(50, 99), Scale(1, 9), Scale(10, 49));

        var scales = JsonSerializer.SerializeToElement(response, Web)
            .GetProperty("items")[0]
            .GetProperty("priceScales");

        Assert.Equal(
            [1, 10, 50],
            scales.EnumerateArray().Select(scale => scale.GetProperty("fromUnit").GetInt32()));
    }

    private static QuotationPriceScaleRef Scale(int fromUnit, int toUnit) =>
        new(fromUnit, toUnit, Discount: 5m, QuotationPriceScaleRestriction.Multiple, Multiple: 1,
            PackagingUnit: null);

    private static async Task<QuotationResponse> ComposeAsync(
        Quotation quotation, params QuotationPriceScaleRef[] scales)
    {
        var composer = new QuotationResponseComposer(
            new StubQuotationCustomerLookup(new QuotationCustomerRef(
                ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
                "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false)),
            new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
            new StubQuotationProductLookup(new Dictionary<Guid, QuotationProductRef>
            {
                [ProductId] = new(ProductId, "Tornillo 1/4", "TOR-001", ImageUrl: null, scales)
            }),
            new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>()));

        return await composer.ComposeAsync(
            TenantId, quotation.ToDto(), TestContext.Current.CancellationToken);
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
}
