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
        var harness = NewHandler();

        await harness.Handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken);

        Assert.NotNull(harness.Sender.Sent);
        Assert.Equal(RecordingPdfStorage.PublicUrl, harness.Sender.Sent.DocumentUrl);
        Assert.NotEqual(PresignedUrl, harness.Sender.Sent.DocumentUrl);
    }

    // Lo que se publica es el objeto que `QuotationPdfProvider` dejo al dia, no un archivo que
    // el navegador haya subido.
    [Fact]
    public async Task SendPublishesTheCurrentGeneratedDocument()
    {
        var harness = NewHandler();

        await harness.Handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken);

        Assert.NotNull(harness.Repository.Pdf);
        Assert.Equal(harness.Repository.Pdf.StorageKey, harness.Storage.PublishedKey);
    }

    [Fact]
    public async Task SendHandsTheSenderTheQuotationTotalAndValidity()
    {
        var harness = NewHandler();

        await harness.Handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken);

        Assert.NotNull(harness.Sender.Sent);
        Assert.Equal("QUO-2026-0001", harness.Sender.Sent.OrderNumber);
        Assert.Equal(ValidUntil, harness.Sender.Sent.ValidUntil);
        Assert.Equal("Ferretería El Tornillo", harness.Sender.Sent.FullName);
        Assert.Equal("3001234567", harness.Sender.Sent.ToPhone);
    }

    // Con datos propios de facturacion, la cotizacion se le puede mandar a ese telefono en vez
    // del cliente: quien factura no siempre es quien recibe el WhatsApp. Lo elige la pantalla y
    // viaja como opcion cerrada --Customer o Billing--, no como un telefono suelto.
    [Fact]
    public async Task SendToTheBillingPartyUsesItsPhoneAndName()
    {
        var harness = NewHandler(billing: BillingParty);

        await harness.Handler.HandleAsync(
            NewCommand("Billing"), TestContext.Current.CancellationToken);

        Assert.NotNull(harness.Sender.Sent);
        Assert.Equal("3013574996", harness.Sender.Sent.ToPhone);
        Assert.Equal("Danilo Amaris Ojeda", harness.Sender.Sent.FullName);
    }

    // Sin elegir, sigue yendo al cliente: es lo que hacia antes de que la opcion existiera.
    [Fact]
    public async Task SendWithoutARecipientStillGoesToTheCustomer()
    {
        var harness = NewHandler(billing: BillingParty);

        await harness.Handler.HandleAsync(
            NewCommand(), TestContext.Current.CancellationToken);

        Assert.NotNull(harness.Sender.Sent);
        Assert.Equal("3001234567", harness.Sender.Sent.ToPhone);
    }

    // Pedir el telefono de facturacion cuando la cotizacion factura a los datos del cliente es
    // una opcion que la pantalla no deberia haber ofrecido: 422 con codigo, no un envio
    // silencioso al cliente que nadie pidio.
    [Fact]
    public async Task SendToTheBillingPartyWithoutOneIsRejected()
    {
        var harness = NewHandler();

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Handler.HandleAsync(
                NewCommand("Billing"), TestContext.Current.CancellationToken));

        Assert.Equal("quotation.quotation.billing_phone_missing", error.Code);
        Assert.Null(harness.Sender.Sent);
    }

    [Fact]
    public async Task SendToTheBillingPartyWithoutAPhoneIsRejected()
    {
        var harness = NewHandler(
            billing: new QuotationPartyDetails { Name = "Danilo Amaris Ojeda" });

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Handler.HandleAsync(
                NewCommand("Billing"), TestContext.Current.CancellationToken));

        Assert.Equal("quotation.quotation.billing_phone_missing", error.Code);
    }

    [Fact]
    public async Task SendWithAnUnknownRecipientIsRejected()
    {
        var harness = NewHandler(billing: BillingParty);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Handler.HandleAsync(
                NewCommand("Whoever"), TestContext.Current.CancellationToken));

        Assert.Equal("quotation.quotation.recipient_invalid", error.Code);
    }

    // El envio por WhatsApp y la firma de la URL son efectos externos irreversibles: si el
    // agregado no puede pasar a Sent, no se puede haber mandado nada. Sin este guard el cliente
    // recibia la cotizacion y el sistema la dejaba en borrador -- el estado a medias que el
    // orden de este handler existe para evitar.
    [Fact]
    public async Task SendDoesNotReachWhatsAppWhenTheQuotationCannotBeSent()
    {
        var harness = NewHandler(withValidUntil: false);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken));

        Assert.Equal("quotation.quotation.valid_until_required", error.Code);
        Assert.Null(harness.Sender.Sent);
        Assert.Null(harness.Storage.PublishedKey);
    }

    // US-12 (reenvío): la cotización ya enviada y sin cambios se vuelve a mandar. Lo que
    // distingue un reenvío de un envío no es lo que sale hacia WhatsApp —es idéntico— sino
    // cómo queda registrado: evento Resent en el historial y acción `.resent` en auditoría.
    [Fact]
    public async Task ResendingAnAlreadySentQuotationRecordsItAsAResend()
    {
        var harness = NewHandler(alreadySent: true);

        await harness.Handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken);

        Assert.NotNull(harness.Sender.Sent);
        Assert.Equal(
            QuotationHistoryEventType.Resent,
            Assert.Single(harness.Repository.HistoryEntries).EventType);
        Assert.Equal("quotation.quotation.resent", Assert.Single(harness.Audit.Actions));
    }

    [Fact]
    public async Task SendingADraftRecordsItAsAFirstSend()
    {
        var harness = NewHandler();

        await harness.Handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken);

        Assert.Equal(
            QuotationHistoryEventType.Sent,
            Assert.Single(harness.Repository.HistoryEntries).EventType);
        Assert.Equal("quotation.quotation.sent", Assert.Single(harness.Audit.Actions));
    }

    // ---- El envio que falla ----
    //
    // El motivo de todo esto: un envio que se caia no dejaba rastro. La transaccion del request
    // se descarta a proposito --la cotizacion tiene que quedar en borrador-- y con ella se iba
    // cualquier registro de que alguien intento enviarla y no pudo.

    [Fact]
    public async Task AFailedSendIsAnnotatedWithTheStageThatBrokeIt()
    {
        var harness = NewHandler(
            whatsAppFailure: new QuotationsDomainException(
                "quotation.whatsapp.send_failed", "Zenvia responded 401: unauthorized"));

        await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken));

        var entry = harness.FailureLog.HistoryEntry;
        Assert.NotNull(entry);
        Assert.Equal(AdvisorId, entry.MemberId);
        // El paso, en el texto que lee quien vende. La traza y la respuesta cruda de Zenvia no
        // estan aca: las guarda el log de la aplicacion, con el resto de las fallas de la API.
        Assert.NotNull(entry.Details);
        Assert.Contains("WhatsApp", entry.Details, StringComparison.Ordinal);
    }

    // La cara del mismo hecho que ve quien vende: sin traza, sin respuesta cruda, en espanol.
    [Fact]
    public async Task AFailedSendLeavesAReadableEntryInTheTimeline()
    {
        var harness = NewHandler(
            whatsAppFailure: new QuotationsDomainException(
                "quotation.whatsapp.send_failed", "Zenvia responded 401: unauthorized"));

        await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken));

        var entry = harness.FailureLog.HistoryEntry;
        Assert.NotNull(entry);
        Assert.Equal(QuotationHistoryEventType.SendFailed, entry.EventType);
        Assert.NotNull(entry.Details);
        Assert.DoesNotContain("Zenvia", entry.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("401", entry.Details, StringComparison.Ordinal);

        // Y no viaja por el repositorio: iria en la transaccion que se descarta.
        Assert.Empty(harness.Repository.HistoryEntries);
    }

    // Un error de dominio ya se explica solo. Relanzarlo tal cual es lo que evita esconder
    // "el cliente no tiene telefono" detras de un generico que no dice que hacer.
    [Fact]
    public async Task ADomainFailureKeepsItsOwnCode()
    {
        var harness = NewHandler(
            whatsAppFailure: new QuotationsDomainException(
                "quotation.whatsapp.recipient_missing", "The client has no phone number."));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken));

        Assert.Equal("quotation.whatsapp.recipient_missing", error.Code);
    }

    // El caso que abrio esto: un timeout salia como 500 server.unexpected y la pantalla no tenia
    // nada que mostrar. Ahora sale con codigo propio, nombrando el paso, y con la original
    // adentro para que el log de la aplicacion la guarde entera.
    [Fact]
    public async Task AnUnexpectedFailureBecomesAClearErrorThatNamesTheStage()
    {
        var harness = NewHandler(
            whatsAppFailure: new TaskCanceledException("A task was canceled."));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken));

        Assert.Equal("quotation.send.failed", error.Code);
        Assert.Contains("WhatsApp", error.Message, StringComparison.Ordinal);
        // La original no se pierde: viaja como interna, que es de donde la levanta el log.
        Assert.IsType<TaskCanceledException>(error.InnerException);
    }

    // El envio fallido no marca la cotizacion como enviada: es la invariante que ya existia, y
    // registrar la falla no puede haberla aflojado.
    [Fact]
    public async Task AFailedSendLeavesTheQuotationAsADraft()
    {
        var harness = NewHandler(
            whatsAppFailure: new TaskCanceledException("A task was canceled."));

        await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken));

        Assert.Equal(QuotationStatus.Draft, harness.Repository.Quotation.Status);
        Assert.Empty(harness.Audit.Actions);
    }

    // Una cotizacion sin vigencia no es un envio que fallo, es uno que nunca empezo: anotarlo
    // llenaria el registro de ruido que no se arregla mirando una traza.
    [Fact]
    public async Task AQuotationThatCannotBeSentIsNotRecordedAsAFailure()
    {
        var harness = NewHandler(withValidUntil: false);

        await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken));

        Assert.Null(harness.FailureLog.HistoryEntry);
    }

    private static SendQuotationCommand NewCommand(string? recipient = null) =>
        new(TenantId, CurrentQuotationId, recipient);

    private static readonly QuotationPartyDetails BillingParty = new()
    {
        Name = "Danilo Amaris Ojeda",
        Phone = "3013574996",
        Email = "daniloamaris@ejemplo.co",
        Address = "calle 90#45",
    };

    private static Guid CurrentQuotationId;

    /// <summary>
    /// Lo que arma <see cref="NewHandler"/>. Un registro y no una tupla: son seis piezas y con
    /// posiciones cada prueba abria con una fila de guiones bajos que habia que contar.
    /// </summary>
    private sealed record Harness(
        SendQuotationHandler Handler,
        RecordingWhatsAppSender Sender,
        RecordingPdfStorage Storage,
        StubQuotationRepository Repository,
        RecordingQuotationAuditPublisher Audit,
        RecordingQuotationSendFailureLog FailureLog);

    private static Harness NewHandler(
        bool withValidUntil = true,
        bool alreadySent = false,
        Exception? whatsAppFailure = null,
        QuotationPartyDetails? billing = null)
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
            billing is null
                ? QuotationParties.Empty
                : new QuotationParties(billing, null),
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

        var failureLog = new RecordingQuotationSendFailureLog();

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
            whatsAppFailure is null
                ? sender
                : new FailingWhatsAppSender(whatsAppFailure),
            new StubMembershipDirectory(AdvisorId.Value),
            failureLog,
            new StubExecutionContext(SubjectId, TenantId),
            new FixedClock(Now));

        return new Harness(handler, sender, storage, repository, audit, failureLog);
    }
}
