using System.Text.Json;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Lo que cuesta cada unidad ya con el descuento aplicado. La pantalla lo muestra en una columna
/// propia y el PDF lo imprime desde 2026-09-10 (<c>QuotationPdfDocumentMapper</c>), pero cada uno
/// lo derivaba por su cuenta: el cálculo vive ahora en la línea y viaja en el DTO y en la
/// respuesta HTTP, como <c>DiscountAmount</c> o <c>TaxAmount</c>.
///
/// Se deriva del total de la línea y no de <c>UnitPrice × (1 − %)</c>: el descuento se redondea
/// sobre el bruto, así que la resta es la única que reconstruye exactamente lo que se cobra.
/// </summary>
public sealed class QuotationDiscountedUnitPriceTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // Los mismos números que el PDF ya imprime (QuotationPdfDocumentMapperTests): 12 × 35.900 =
    // 430.800, 15% = 64.620, línea = 366.180, unitario con descuento = 30.515.
    [Fact]
    public void TheItemReportsWhatEachUnitCostsAfterTheDiscount()
    {
        var quotation = NewQuotation();
        AddItem(quotation, quantity: 12m, unitPrice: 35_900m, discountPercentage: 15m);

        var item = quotation.ToDto().Items.Single();

        Assert.Equal(64_620m, item.DiscountAmount);
        Assert.Equal(30_515m, item.DiscountedUnitPrice);
    }

    // Sin descuento no hay nada que restar: la columna repite el precio unitario en vez de
    // quedar vacía, que es lo que la pantalla necesita para no tratar el 0% como un caso aparte.
    [Fact]
    public void WithoutADiscountEachUnitStillCostsTheUnitPrice()
    {
        var quotation = NewQuotation();
        AddItem(quotation, quantity: 3m, unitPrice: 45_000m, discountPercentage: 0m);

        var item = quotation.ToDto().Items.Single();

        Assert.Equal(45_000m, item.DiscountedUnitPrice);
    }

    // La respuesta HTTP lo trae en cada línea y en camelCase, que es lo que lee la pantalla.
    // Contra el composer de producción y no contra su doble: los campos de la línea son todos
    // decimales y el constructor es posicional, así que pasar `Subtotal` donde va éste compila
    // sin chistar. Esta prueba es lo único que lo distingue.
    [Fact]
    public async Task TheResponseSerializesTheDiscountedUnitPriceOnEachItem()
    {
        var quotation = NewQuotation();
        AddItem(quotation, quantity: 12m, unitPrice: 35_900m, discountPercentage: 15m);

        var composer = new QuotationResponseComposer(
            new StubQuotationCustomerLookup(new QuotationCustomerRef(
                ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
                "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false)),
            new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
            new UnknownProductLookup(),
            new UnresolvedCompanyLookup());

        var response = await composer.ComposeAsync(
            TenantId, quotation.ToDto(), TestContext.Current.CancellationToken);
        var json = JsonSerializer.SerializeToElement(response, Web);

        var item = json.GetProperty("items")[0];
        Assert.Equal(30_515m, item.GetProperty("discountedUnitPrice").GetDecimal());
        // Al lado, para que la prueba falle si los dos importes se cruzan: están en unidades
        // distintas — éste con IVA adentro, la base sin él.
        Assert.Equal(307_714.29m, item.GetProperty("subtotal").GetDecimal());
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
        Quotation quotation, decimal quantity, decimal unitPrice, decimal discountPercentage) =>
        quotation.AddItem(
            QuotationItemId.New(),
            Guid.CreateVersion7(),
            quantity,
            unitPrice,
            discountPercentage,
            taxPercentage: 19,
            AdvisorId,
            Now);

    // El producto y la empresa, en su caso "no resuelve" — que el composer ya soporta porque una
    // cotización es histórica y tiene que leerse aunque se hayan borrado. Lo que queda en la
    // respuesta son los importes de la línea, que es lo que se está probando.
    private sealed class UnknownProductLookup : IQuotationProductLookup
    {
        public Task<IReadOnlyDictionary<Guid, QuotationProductRef>> FindManyAsync(
            Guid tenantId,
            IReadOnlyCollection<Guid> productIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, QuotationProductRef>>(
                new Dictionary<Guid, QuotationProductRef>());
    }

    private sealed class UnresolvedCompanyLookup : IQuotationCompanyLookup
    {
        public Task<QuotationCompanyRef?> FindAsync(
            Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
            Task.FromResult<QuotationCompanyRef?>(null);
    }
}
