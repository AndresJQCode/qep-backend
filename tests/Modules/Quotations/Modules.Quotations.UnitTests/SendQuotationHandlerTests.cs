using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// US-12: qué se le entrega al canal de WhatsApp al enviar la cotización. El status HTTP no
/// alcanza para verificarlo — un envío puede responder 200 con el mensaje incompleto.
/// </summary>
public sealed class SendQuotationHandlerTests
{
    private const string PresignedUrl = "https://r2.example.com/cot.pdf?X-Amz-Signature=abc";

    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly ValidUntil = new(2026, 9, 30);

    // La URL **publica**, no la firmada: Meta no puede descargar el PDF desde una prefirmada de
    // R2 --le falla y descarta el mensaje entero, minutos despues del 200 de Zenvia-- asi que el
    // envio publica una copia con clave aleatoria y manda esa.
    [Fact]
    public async Task SendHandsTheSenderThePublicPdfUrl()
    {
        var (handler, sender, _, _, _) = NewHandler();

        await handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken);

        Assert.NotNull(sender.Sent);
        Assert.Equal(RecordingPdfStorage.PublicUrl, sender.Sent.DocumentUrl);
        Assert.NotEqual(PresignedUrl, sender.Sent.DocumentUrl);
    }

    // Lo que se publica es el objeto que `QuotationPdfProvider` dejo al dia, no un archivo que
    // el navegador haya subido.
    [Fact]
    public async Task SendPublishesTheCurrentGeneratedDocument()
    {
        var (handler, _, storage, repository, _) = NewHandler();

        await handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken);

        Assert.NotNull(repository.Pdf);
        Assert.Equal(repository.Pdf.StorageKey, storage.PublishedKey);
    }

    [Fact]
    public async Task SendHandsTheSenderTheQuotationTotalAndValidity()
    {
        var (handler, sender, _, _, _) = NewHandler();

        await handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken);

        Assert.NotNull(sender.Sent);
        Assert.Equal("QUO-2026-0001", sender.Sent.OrderNumber);
        Assert.Equal(ValidUntil, sender.Sent.ValidUntil);
        Assert.Equal("Ferretería El Tornillo", sender.Sent.FullName);
        Assert.Equal("3001234567", sender.Sent.ToPhone);
    }

    // El envio por WhatsApp y la firma de la URL son efectos externos irreversibles: si el
    // agregado no puede pasar a Sent, no se puede haber mandado nada. Sin este guard el cliente
    // recibia la cotizacion y el sistema la dejaba en borrador -- el estado a medias que el
    // orden de este handler existe para evitar.
    [Fact]
    public async Task SendDoesNotReachWhatsAppWhenTheQuotationCannotBeSent()
    {
        var (handler, sender, storage, _, _) = NewHandler(withValidUntil: false);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken));

        Assert.Equal("quotation.quotation.valid_until_required", error.Code);
        Assert.Null(sender.Sent);
        Assert.Null(storage.PublishedKey);
    }

    // US-12 (reenvío): la cotización ya enviada y sin cambios se vuelve a mandar. Lo que
    // distingue un reenvío de un envío no es lo que sale hacia WhatsApp —es idéntico— sino
    // cómo queda registrado: evento Resent en el historial y acción `.resent` en auditoría.
    [Fact]
    public async Task ResendingAnAlreadySentQuotationRecordsItAsAResend()
    {
        var (handler, sender, _, repository, audit) = NewHandler(alreadySent: true);

        await handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken);

        Assert.NotNull(sender.Sent);
        Assert.Equal(
            QuotationHistoryEventType.Resent,
            Assert.Single(repository.HistoryEntries).EventType);
        Assert.Equal("quotation.quotation.resent", Assert.Single(audit.Actions));
    }

    [Fact]
    public async Task SendingADraftRecordsItAsAFirstSend()
    {
        var (handler, _, _, repository, audit) = NewHandler();

        await handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken);

        Assert.Equal(
            QuotationHistoryEventType.Sent,
            Assert.Single(repository.HistoryEntries).EventType);
        Assert.Equal("quotation.quotation.sent", Assert.Single(audit.Actions));
    }

    private static SendQuotationCommand NewCommand() =>
        new(TenantId, CurrentQuotationId);

    private static Guid CurrentQuotationId;

    private static (
        SendQuotationHandler Handler,
        RecordingWhatsAppSender Sender,
        RecordingPdfStorage Storage,
        StubQuotationRepository Repository,
        RecordingQuotationAuditPublisher Audit) NewHandler(
        bool withValidUntil = true, bool alreadySent = false)
    {
        var quotation = Quotation.Create(
            QuotationId.New(),
            TenantId,
            "QUO-2026-0001",
            ClientId,
            AdvisorId,
            withValidUntil ? ValidUntil : null,
            paymentMethod: "Transferencia bancaria",
            notes: null,
            QuotationParties.Empty,
            billingAccount: null,
            customerWithRetention: false,
            customerVatSurplus: false,
            AdvisorId,
            Now);

        if (alreadySent)
        {
            // Un envío anterior, no el que la prueba ejerce: por eso no pasa por el handler y
            // no deja rastro en los dobles que la aserción mira.
            quotation.Send(AdvisorId, Now.AddHours(-2));
        }

        CurrentQuotationId = quotation.Id.Value;
        var sender = new RecordingWhatsAppSender();
        var storage = new RecordingPdfStorage(PresignedUrl);
        var repository = new StubQuotationRepository(quotation);
        var audit = new RecordingQuotationAuditPublisher();
        var customer = new QuotationCustomerRef(
            ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
            "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false);

        var handler = new SendQuotationHandler(
            repository,
            new NoOpQuotationsUnitOfWork(),
            audit,
            new QuotationPdfProvider(
                repository,
                new StubQuotationResponseComposer(),
                new CountingPdfRenderer(),
                storage,
                new FixedClock(Now)),
            storage,
            new StubQuotationCustomerLookup(customer),
            sender,
            new StubMembershipDirectory(AdvisorId.Value),
            new StubExecutionContext(SubjectId, TenantId),
            new FixedClock(Now));

        return (handler, sender, storage, repository, audit);
    }
}
