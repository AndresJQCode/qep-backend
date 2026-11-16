using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.RichText;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// «Descargar PDF» del pedido (spec 2026-11-16): el mismo documento que sale por WhatsApp, entregado como
/// enlace firmado. A diferencia de la cotización no hay caché, así que lo que se fija es quién puede, qué
/// pedido y con qué nombre llega el archivo.
/// </summary>
public sealed class ExportOrderPdfHandlerTests
{
    private const string DownloadUrl = "https://r2.example.com/ped.pdf?X-Amz-Signature=abc";

    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExportRendersTheOrderAndReturnsTheSignedUrl()
    {
        var harness = NewHarness();

        var result = await harness.Handler.HandleAsync(
            new ExportOrderPdfCommand(TenantId, harness.Order.Id.Value), TestContext.Current.CancellationToken);

        Assert.Equal(DownloadUrl, result.Url);
        Assert.Equal(Now, result.GeneratedAt);
        Assert.Equal(1, harness.Renderer.Calls);
        Assert.Equal([harness.Order.Id], harness.Storage.SavedOrders);
    }

    // El archivo llega con el número del pedido, no con la clave opaca del bucket.
    [Fact]
    public async Task TheDownloadIsNamedAfterTheOrder()
    {
        var harness = NewHarness();

        await harness.Handler.HandleAsync(
            new ExportOrderPdfCommand(TenantId, harness.Order.Id.Value), TestContext.Current.CancellationToken);

        Assert.Equal($"Pedido-{harness.Order.OrderNumber}.pdf", harness.Storage.RequestedFileName);
    }

    // Descargar es leer: un pedido anulado sigue siendo el registro de lo acordado. El envío sí lo frena.
    [Fact]
    public async Task ACancelledOrderCanBeDownloaded()
    {
        var harness = NewHarness(cancelled: true);

        var result = await harness.Handler.HandleAsync(
            new ExportOrderPdfCommand(TenantId, harness.Order.Id.Value), TestContext.Current.CancellationToken);

        Assert.Equal(DownloadUrl, result.Url);
    }

    [Fact]
    public async Task WithoutOrderReadItIsForbiddenAndNothingIsRendered()
    {
        var harness = NewHarness(
            executionContext: new StubExecutionContext(SubjectId, TenantId, OrdersPermissions.OrderRead));

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            harness.Handler.HandleAsync(
                new ExportOrderPdfCommand(TenantId, harness.Order.Id.Value), TestContext.Current.CancellationToken));

        Assert.Equal(0, harness.Renderer.Calls);
    }

    [Fact]
    public async Task AnUnknownOrderIsNotFound()
    {
        var harness = NewHarness();

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            harness.Handler.HandleAsync(
                new ExportOrderPdfCommand(TenantId, Guid.CreateVersion7()), TestContext.Current.CancellationToken));

        Assert.Equal("order.order.not_found", error.Code);
        Assert.Equal(0, harness.Renderer.Calls);
    }

    private sealed record Harness(
        ExportOrderPdfHandler Handler,
        Order Order,
        CountingPdfRenderer Renderer,
        RecordingPdfStorage Storage);

    private static Harness NewHarness(bool cancelled = false, IExecutionContext? executionContext = null)
    {
        var order = OrderBuilders.Pending(TenantId, AdvisorId, Now.AddDays(-1), clientId: ClientId);
        if (cancelled)
        {
            order.Cancel(AdvisorId, "El cliente desistió", Now.AddHours(-1));
        }

        var customers = new StubQuotationCustomerLookup(new QuotationCustomerRef(
            ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
            "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false));
        var renderer = new CountingPdfRenderer();
        var storage = new RecordingPdfStorage(DownloadUrl);

        var handler = new ExportOrderPdfHandler(
            new StubOrderListRepository(order),
            new StubQuotationRepository(AnyQuotation()),
            new OrderResponseComposer(
                customers,
                new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
                new StubQuotationProductLookup(new Dictionary<Guid, QuotationVariantRef>()),
                new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>()),
                new StubQuotationWithholdingLookup(),
                new StubQuotationGeographyLookup(new Dictionary<Guid, string>()),
                new StubQuotationBuyerAccountLookup(),
                new FixedTenantClock(Now)),
            new OrderPdfProvider(
                renderer, storage, new FixedTenantClock(Now), new StubQuotationLogoLookup(),
                new StubTenantDirectory(), new StubPdfContentStore(), new RichTextHtmlReader()),
            storage,
            executionContext ?? new StubExecutionContext(SubjectId, TenantId),
            new FixedClock(Now));

        return new Harness(handler, order, renderer, storage);
    }

    // El doble de IQuotationRepository exige una cotización; el pedido de estas pruebas es manual,
    // así que nunca se consulta.
    private static Quotation AnyQuotation() =>
        Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", ClientId, AdvisorId, new DateOnly(2026, 11, 30),
            paymentMethod: null, notes: null, QuotationParties.Empty, billingAccount: null, defaultCurrency: "COP",
            customerVatSurplus: false, customerWithholdings: [], MunicipalContext.None, AdvisorId, Now);
}
