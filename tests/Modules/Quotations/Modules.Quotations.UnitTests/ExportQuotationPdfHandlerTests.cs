using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Lo que este caso de uso decide es **cuándo hay que volver a generar el PDF**, y eso es lo que
/// estas pruebas fijan. Generar cuesta una llamada de red a `qcode-pdf` y una subida a R2 por
/// cada exportación y cada reenvío; sin la comparación de versión, abrir dos veces la misma
/// cotización sin tocarla pagaría las dos.
/// </summary>
public sealed class ExportQuotationPdfHandlerTests
{
    private const string DownloadUrl = "https://r2.example.com/cot.pdf?X-Amz-Signature=abc";

    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheFirstExportGeneratesTheDocumentAndRecordsIt()
    {
        var (handler, renderer, _, repository, quotation) = NewHandler();

        var result = await handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken);

        Assert.Equal(1, renderer.Calls);
        Assert.NotNull(repository.Pdf);
        Assert.Equal(quotation.Version, repository.Pdf.QuotationVersion);
        Assert.Equal(DownloadUrl, result.Url);
    }

    // El motivo de existir de `quotation_pdfs`: exportar dos veces sin tocar la cotizacion no
    // vuelve a llamar al servicio de PDF ni a subir nada.
    [Fact]
    public async Task ExportingTwiceWithoutChangesDoesNotRegenerate()
    {
        var (handler, renderer, storage, _, _) = NewHandler();

        await handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken);
        await handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken);

        Assert.Equal(1, renderer.Calls);
        Assert.Equal(1, storage.Saves);
    }

    [Fact]
    public async Task ExportingAfterAChangeRegenerates()
    {
        var (handler, renderer, _, _, quotation) = NewHandler();
        await handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken);

        // Cualquier mutacion del agregado incrementa Version, que es justo lo que invalida.
        quotation.UpdateDetails(
            new DateOnly(2026, 9, 30),
            "Transferencia bancaria",
            "Se agrego una observacion",
            QuotationParties.Empty,
            billingAccount: null,
            repricing: null,
            AdvisorId,
            Now.AddHours(1));

        await handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken);

        Assert.Equal(2, renderer.Calls);
    }

    // El archivo llega con el numero de cotizacion, no con la clave opaca del bucket.
    [Fact]
    public async Task TheDownloadIsNamedAfterTheQuotation()
    {
        var (handler, _, storage, _, _) = NewHandler();

        await handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken);

        Assert.Equal("Cotizacion-QUO-2026-0001.pdf", storage.RequestedFileName);
    }

    private static ExportQuotationPdfCommand NewCommand(Guid? quotationId = null) =>
        new(TenantId, quotationId ?? CurrentQuotationId);

    private static Guid CurrentQuotationId;

    private static (
        ExportQuotationPdfHandler Handler,
        CountingPdfRenderer Renderer,
        RecordingPdfStorage Storage,
        StubQuotationRepository Repository,
        Quotation Quotation) NewHandler()
    {
        var quotation = Quotation.Create(
            QuotationId.New(),
            TenantId,
            "QUO-2026-0001",
            ClientId,
            AdvisorId,
            new DateOnly(2026, 9, 30),
            paymentMethod: "Transferencia bancaria",
            notes: null,
            QuotationParties.Empty,
            billingAccount: null,
            customerWithRetention: false,
            customerVatSurplus: false,
            AdvisorId,
            Now);
        CurrentQuotationId = quotation.Id.Value;

        var repository = new StubQuotationRepository(quotation);
        var renderer = new CountingPdfRenderer();
        var storage = new RecordingPdfStorage(DownloadUrl);

        var handler = new ExportQuotationPdfHandler(
            repository,
            new NoOpQuotationsUnitOfWork(),
            new StubQuotationResponseComposer(),
            renderer,
            storage,
            new StubExecutionContext(SubjectId, TenantId),
            new FixedClock(Now));

        return (handler, renderer, storage, repository, quotation);
    }
}
