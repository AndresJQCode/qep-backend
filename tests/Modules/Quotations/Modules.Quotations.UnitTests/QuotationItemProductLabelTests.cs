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
        var live = new QuotationVariantRef(ProductId, ProductId, "Hoy", "HOY-1", ImageUrl: null, Scales: []);

        Assert.Equal("SNAP-1", QuotationItemProductLabel.CodeOf("SNAP-1", live));
        Assert.Equal("Antes", QuotationItemProductLabel.NameOf("Antes", live));
        Assert.Equal("HOY-1", QuotationItemProductLabel.CodeOf(null, live));
        Assert.Equal("Hoy", QuotationItemProductLabel.NameOf(null, live));
        Assert.Equal(string.Empty, QuotationItemProductLabel.CodeOf(null, null));
        Assert.Equal(string.Empty, QuotationItemProductLabel.NameOf(null, null));
    }

    // ---- Por variante (spec 2026-10-20 §4) ----

    private static readonly Guid CamisetaM = Guid.CreateVersion7();

    private static Dictionary<Guid, QuotationVariantRef> CatalogWithM(string code, string name) => new()
    {
        [CamisetaM] = new(CamisetaM, ProductId, name, code, ImageUrl: null, Scales: []),
    };

    // Un borrador lee la variante en vivo: renombrar el producto (o un valor de opción) se ve en la
    // línea mientras no sale del borrador. Al enviarla, la foto toma el nombre visible de ese
    // momento y desde ahí no lo sigue.
    [Fact]
    public async Task DraftLabelFollowsRenamedVariantUntilSent()
    {
        var quotation = NewVariantQuotation();

        var before = Assert.Single((await ComposeWithAsync(quotation, CatalogWithM("CAM-M", "Camiseta – M"))).Items);
        var renamedCatalog = CatalogWithM("CAM-M", "Camiseta polo – M");
        var renamed = Assert.Single((await ComposeWithAsync(quotation, renamedCatalog)).Items);

        Assert.Equal(("CAM-M", "Camiseta – M"), (before.ProductCode, before.ProductName));
        Assert.Equal(("CAM-M", "Camiseta polo – M"), (renamed.ProductCode, renamed.ProductName));
        Assert.Equal((CamisetaM, ProductId), (renamed.VariantId, renamed.ProductId));

        var snapshots = await QuotationItemProductLabel.ResolveMissingAsync(
            new StubQuotationProductLookup(renamedCatalog), TenantId, quotation, TestContext.Current.CancellationToken);
        quotation.Send(AdvisorId, Now, snapshots);

        var sent = Assert.Single((await ComposeWithAsync(quotation, CatalogWithM("CAM-M", "Camiseta polo – Mediana"))).Items);
        Assert.Equal(("CAM-M", "Camiseta polo – M"), (sent.ProductCode, sent.ProductName));
    }

    // La foto de una línea enviada no se recalcula: ni el código ni el nombre visible siguen al
    // catálogo después del envío.
    [Fact]
    public async Task SentQuotationSnapshotDoesNotChangeAfterRename()
    {
        var quotation = NewVariantQuotation();
        quotation.Send(AdvisorId, Now, await QuotationItemProductLabel.ResolveMissingAsync(
            new StubQuotationProductLookup(CatalogWithM("CAM-M", "Camiseta – M")),
            TenantId, quotation, TestContext.Current.CancellationToken));

        var item = Assert.Single((await ComposeWithAsync(quotation, CatalogWithM("CAM-M2", "Camiseta renombrada – M"))).Items);

        Assert.Equal(("CAM-M", "Camiseta – M"), (item.ProductCode, item.ProductName));
        Assert.Equal("CAM-M", Assert.Single(quotation.Items).ProductCode);
    }

    // La foto se busca por variante: un catálogo que sólo conociera el producto no la resuelve, y
    // la línea queda sin foto en vez de tomar el nombre del producto.
    [Fact]
    public async Task TheSnapshotIsResolvedByVariantNotByProduct()
    {
        var quotation = NewVariantQuotation();
        var byProduct = new Dictionary<Guid, QuotationVariantRef>
        {
            [ProductId] = new(ProductId, ProductId, "Camiseta", "CAM", ImageUrl: null, Scales: []),
        };

        var snapshots = await QuotationItemProductLabel.ResolveMissingAsync(
            new StubQuotationProductLookup(byProduct), TenantId, quotation, TestContext.Current.CancellationToken);

        Assert.Empty(snapshots);
    }

    private static Quotation NewVariantQuotation()
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0002", ClientId, AdvisorId,
            new DateOnly(2026, 10, 31), "Transferencia bancaria", null, QuotationParties.Empty,
            new QuotationBillingAccount
            {
                CompanyId = Guid.CreateVersion7(),
                BankName = "Bancolombia",
                AccountNumber = "12345678",
                Currency = "COP",
            },
            "COP",
            false, false, AdvisorId, Now);
        quotation.AddItem(
            QuotationItemId.New(), CamisetaM, ProductId, quantity: 1m, unitPrice: 1_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);
        return quotation;
    }

    private static async Task<QuotationResponse> ComposeWithAsync(
        Quotation quotation, IReadOnlyDictionary<Guid, QuotationVariantRef> catalog)
    {
        var composer = new QuotationResponseComposer(
            new StubQuotationCustomerLookup(new QuotationCustomerRef(
                ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
                "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false)),
            new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
            new StubQuotationProductLookup(catalog),
            new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>()),
            new FixedQuotationSettingsStore());

        return await composer.ComposeAsync(
            TenantId, quotation.ToDto(), TestContext.Current.CancellationToken);
    }

    private static async Task<QuotationResponse> ComposeAsync(
        Quotation quotation, string liveCode, string liveName)
    {
        var composer = new QuotationResponseComposer(
            new StubQuotationCustomerLookup(new QuotationCustomerRef(
                ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
                "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false)),
            new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
            new StubQuotationProductLookup(new Dictionary<Guid, QuotationVariantRef>
            {
                [ProductId] = new(ProductId, ProductId, liveName, liveCode, ImageUrl: null, Scales: []),
            }),
            new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>()),
            new FixedQuotationSettingsStore());

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
            "COP",
            false, false, AdvisorId, Now);
        quotation.AddItem(
            QuotationItemId.New(), ProductId, ProductId, quantity: 1m, unitPrice: 1_000m,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);
        return quotation;
    }
}
