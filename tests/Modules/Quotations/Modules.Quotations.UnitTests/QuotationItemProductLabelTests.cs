using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Qué código y qué nombre de producto muestra una línea (owner, 2026-09-26): el congelado si la
/// cotización ya salió del borrador, el del catálogo de hoy si no. La respuesta de la pantalla y el
/// PDF salen del mismo composer, así que se prueban juntos: si divergieran, el cliente recibiría un
/// documento que no dice lo mismo que la pantalla.
/// </summary>
public sealed class QuotationItemProductLabelTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly Guid ProductId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly TenantCalendar Calendar = new(
        Now, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    // El catálogo cambió después del envío: la cotización sigue diciendo lo que se le mandó.
    [Fact]
    public async Task ASentLineShowsTheSnapshotEvenIfTheCatalogChanged()
    {
        var quotation = NewQuotation();
        quotation.Send(AdvisorId, Now, new Dictionary<Guid, QuotationProductSnapshot>
        {
            [ProductId] = new("TOR-001", "Tornillo 1/4"),
        });

        var item = Assert.Single((await ComposeAsync(quotation, "TOR-999", "Tornillo renombrado")).Items);

        Assert.Equal(("TOR-001", "Tornillo 1/4"), (item.ProductCode, item.ProductName));
    }

    [Fact]
    public async Task ADraftLineShowsTheCatalogAsItIsToday()
    {
        var quotation = NewQuotation();

        var item = Assert.Single((await ComposeAsync(quotation, "TOR-999", "Tornillo renombrado")).Items);

        Assert.Equal(("TOR-999", "Tornillo renombrado"), (item.ProductCode, item.ProductName));
    }

    // Enviada antes de que el snapshot existiera: sin columna llena, se sigue leyendo en vivo.
    [Fact]
    public async Task ASentLineWithoutSnapshotFallsBackToTheCatalog()
    {
        var quotation = NewQuotation();
        quotation.Send(AdvisorId, Now, QuotationProductSnapshot.None);

        var item = Assert.Single((await ComposeAsync(quotation, "TOR-999", "Tornillo renombrado")).Items);

        Assert.Equal(("TOR-999", "Tornillo renombrado"), (item.ProductCode, item.ProductName));
    }

    [Fact]
    public async Task ThePdfPrintsTheSnapshotName()
    {
        var quotation = NewQuotation();
        quotation.Send(AdvisorId, Now, new Dictionary<Guid, QuotationProductSnapshot>
        {
            [ProductId] = new("TOR-001", "Tornillo 1/4"),
        });

        var response = await ComposeAsync(quotation, "TOR-999", "Tornillo renombrado");
        var document = QuotationPdfDocumentMapper.From(response, Calendar, logo: null);

        Assert.Equal("Tornillo 1/4", Assert.Single(document.Items).ProductName);
    }

    [Fact]
    public void TheSnapshotWinsOverTheLiveProduct()
    {
        var live = new QuotationProductRef(ProductId, "Hoy", "HOY-1", ImageUrl: null, Scales: []);

        Assert.Equal("SNAP-1", QuotationItemProductLabel.CodeOf("SNAP-1", live));
        Assert.Equal("Antes", QuotationItemProductLabel.NameOf("Antes", live));
        Assert.Equal("HOY-1", QuotationItemProductLabel.CodeOf(null, live));
        Assert.Equal("Hoy", QuotationItemProductLabel.NameOf(null, live));
        Assert.Equal(string.Empty, QuotationItemProductLabel.CodeOf(null, null));
        Assert.Equal(string.Empty, QuotationItemProductLabel.NameOf(null, null));
    }

    private static async Task<QuotationResponse> ComposeAsync(
        Quotation quotation, string liveCode, string liveName)
    {
        var composer = new QuotationResponseComposer(
            new StubQuotationCustomerLookup(new QuotationCustomerRef(
                ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
                "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false)),
            new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
            new StubQuotationProductLookup(new Dictionary<Guid, QuotationProductRef>
            {
                [ProductId] = new(ProductId, liveName, liveCode, ImageUrl: null, Scales: []),
            }),
            new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>()));

        return await composer.ComposeAsync(
            TenantId, quotation.ToDto(), TestContext.Current.CancellationToken);
    }

    private static Quotation NewQuotation()
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", ClientId, AdvisorId,
            new DateOnly(2026, 10, 31), "Transferencia bancaria", null, QuotationParties.Empty,
            new QuotationBillingAccount
            {
                CompanyId = Guid.CreateVersion7(),
                BankName = "Bancolombia",
                AccountNumber = "12345678",
                Currency = "COP",
            },
            false, false, AdvisorId, Now);
        quotation.AddItem(
            QuotationItemId.New(), ProductId, quantity: 1m, unitPrice: 1_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);
        return quotation;
    }
}
