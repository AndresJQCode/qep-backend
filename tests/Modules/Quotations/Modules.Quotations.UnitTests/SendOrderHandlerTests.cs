using BuildingBlocks.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// «Enviar el pedido por WhatsApp»: qué sale hacia el canal, a quién, con qué documento, y qué queda
/// escrito. Mismo criterio que <see cref="SendQuotationHandlerTests"/>: el status HTTP no alcanza, un
/// envío puede responder 200 con el mensaje incompleto.
/// </summary>
public sealed class SendOrderHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 15, 0, 0, TimeSpan.Zero);

    private static readonly QuotationCustomerRef Customer = new(
        ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
        "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false);

    private static readonly QuotationPartyDetails BillingParty = new()
    {
        Name = "Danilo Amaris Ojeda",
        IdentificationNumber = "1020304050",
        IdentificationType = PartyIdentificationType.Nit,
        Phone = "3013574996",
        Email = "daniloamaris@ejemplo.co",
        Address = "calle 90#45",
        DepartmentId = Guid.CreateVersion7(),
        CityId = Guid.CreateVersion7(),
    };

    // Sin elegir, el cliente: mismo default que la cotización.
    [Fact]
    public async Task SendHandsTheSenderTheOrderNumberTotalCustomerAndPublicPdfUrl()
    {
        var harness = NewHarness();

        await harness.Handler.HandleAsync(NewCommand(harness), TestContext.Current.CancellationToken);

        var sent = harness.Sender.SentOrder;
        Assert.NotNull(sent);
        Assert.Equal(
            ("PED-2026-0001", harness.Order.Total, "COP", "3001234567", "Ferretería El Tornillo"),
            (sent.OrderNumber, sent.Total, sent.Currency, sent.ToPhone, sent.FullName));
        Assert.Equal(RecordingPdfStorage.PublicUrl, sent.DocumentUrl);
    }

    // El documento que sale es el del pedido, generado acá y guardado como PDF de pedido; lo que se
    // publica para Meta es justo ese objeto.
    [Fact]
    public async Task SendRendersAnOrderDocumentAndPublishesIt()
    {
        var harness = NewHarness();

        await harness.Handler.HandleAsync(NewCommand(harness), TestContext.Current.CancellationToken);

        var document = harness.Renderer.Last;
        Assert.NotNull(document);
        Assert.Equal((QuotationPdfKind.Order, "PED-2026-0001"), (document.Kind, document.QuotationNumber));
        Assert.Null(document.ValidUntil);
        Assert.Equal([harness.Order.Id], harness.Storage.SavedOrders);
        Assert.Equal($"quotations/tenants/{TenantId:N}/orders/1.pdf", harness.Storage.PublishedKey);
    }

    [Fact]
    public async Task SendToTheBillingPartyUsesItsPhoneAndName()
    {
        var harness = NewHarness(billing: BillingParty);

        await harness.Handler.HandleAsync(NewCommand(harness, "Billing"), TestContext.Current.CancellationToken);

        Assert.NotNull(harness.Sender.SentOrder);
        Assert.Equal(
            ("3013574996", "Danilo Amaris Ojeda"),
            (harness.Sender.SentOrder.ToPhone, harness.Sender.SentOrder.FullName));
    }

    // El pedido factura a los datos del cliente: la pantalla no debió ofrecer «facturación». 422 con
    // código, no un envío silencioso al cliente que nadie pidió. (Una parte de facturación sin teléfono
    // no se puede armar: el dominio exige la parte completa al crear el pedido.)
    [Fact]
    public async Task SendToTheBillingPartyWithoutOneIsRejectedBeforeRendering()
    {
        var harness = NewHarness();

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Handler.HandleAsync(NewCommand(harness, "Billing"), TestContext.Current.CancellationToken));

        Assert.Equal("order.order.billing_phone_missing", error.Code);
        Assert.Equal(0, harness.Renderer.Calls);
        Assert.Null(harness.Sender.SentOrder);
    }

    [Fact]
    public async Task SendWithAnUnknownRecipientIsRejected()
    {
        var harness = NewHarness(billing: BillingParty);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Handler.HandleAsync(NewCommand(harness, "Whoever"), TestContext.Current.CancellationToken));

        Assert.Equal("order.order.recipient_invalid", error.Code);
        Assert.Null(harness.Sender.SentOrder);
    }

    // Un anulado que le llega al cliente se lee como vigente. Se corta antes de cualquier efecto
    // externo, y con su propio código: no es un envío que falló.
    [Fact]
    public async Task ACancelledOrderIsRejectedBeforeAnyEffect()
    {
        var harness = NewHarness(cancelled: true);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Handler.HandleAsync(NewCommand(harness), TestContext.Current.CancellationToken));

        Assert.Equal("order.order.already_cancelled", error.Code);
        Assert.Equal(0, harness.Renderer.Calls);
        Assert.Null(harness.Storage.PublishedKey);
        Assert.Null(harness.Sender.SentOrder);
        Assert.Empty(harness.Orders.History);
        Assert.Empty(harness.Audit.Actions);
    }

    // Enviar no es una transición: abierto, archivado, pagado y facturado se mandan igual.
    [Theory]
    [InlineData(OrderStatus.Open, false)]
    [InlineData(OrderStatus.Archived, false)]
    [InlineData(OrderStatus.Open, true)]
    public async Task AnOrderThatIsNotCancelledCanBeSent(OrderStatus status, bool invoiced)
    {
        var harness = NewHarness(status: status, invoiced: invoiced);

        await harness.Handler.HandleAsync(NewCommand(harness), TestContext.Current.CancellationToken);

        Assert.NotNull(harness.Sender.SentOrder);
        Assert.Equal(status, harness.Order.Status);
    }

    [Fact]
    public async Task ASendWritesTheHistoryAndTheAuditAndLeavesTheOrderUntouched()
    {
        var harness = NewHarness();
        var version = harness.Order.Version;

        var detail = await harness.Handler.HandleAsync(NewCommand(harness), TestContext.Current.CancellationToken);

        var entry = Assert.Single(harness.Orders.History);
        Assert.Equal(
            (OrderHistoryEventType.Sent, (MemberId?)AdvisorId, "Enviado por WhatsApp al cliente."),
            (entry.EventType, entry.MemberId, entry.Details));
        Assert.Equal(["quotation.order.sent"], harness.Audit.Actions);
        Assert.Equal(version, harness.Order.Version);
        // La pantalla repinta el historial con la respuesta: la entrada nueva viaja ahí.
        Assert.Contains(detail.History, item => item.EventType == "Sent");
    }

    [Fact]
    public async Task ASendToBillingSaysSoInTheHistory()
    {
        var harness = NewHarness(billing: BillingParty);

        await harness.Handler.HandleAsync(NewCommand(harness, "Billing"), TestContext.Current.CancellationToken);

        Assert.Equal("Enviado por WhatsApp a facturación.", Assert.Single(harness.Orders.History).Details);
    }

    // Un pedido de Shopify puede tener un cliente incompleto (sin CUC ni ficha). Enviar sólo necesita
    // un teléfono, así que no se le exige la ficha completa como al vender.
    [Fact]
    public async Task AnIncompleteCustomerCanStillReceiveTheOrder()
    {
        var harness = NewHarness(customer: Customer with { Cuc = null, IsComplete = false });

        await harness.Handler.HandleAsync(NewCommand(harness), TestContext.Current.CancellationToken);

        Assert.NotNull(harness.Sender.SentOrder);
    }

    [Fact]
    public async Task ACustomerFromAnotherTenantIsNotFound()
    {
        var harness = NewHarness(customer: Customer with { TenantId = Guid.CreateVersion7() });

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Handler.HandleAsync(NewCommand(harness), TestContext.Current.CancellationToken));

        Assert.Equal("order.order.client_not_found", error.Code);
        Assert.Null(harness.Sender.SentOrder);
    }

    [Fact]
    public async Task AnUnknownOrderIsNotFound()
    {
        var harness = NewHarness();

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            harness.Handler.HandleAsync(
                new SendOrderCommand(TenantId, Guid.CreateVersion7()), TestContext.Current.CancellationToken));

        Assert.Equal("order.order.not_found", error.Code);
    }

    [Fact]
    public async Task SendingWithoutTheManagePermissionIsForbidden()
    {
        var harness = NewHarness(executionContext: new PermissionlessExecutionContext(SubjectId, TenantId));

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            harness.Handler.HandleAsync(NewCommand(harness), TestContext.Current.CancellationToken));

        Assert.Null(harness.Sender.SentOrder);
    }

    // Un error de dominio ya se explica solo: "el cliente no tiene teléfono" no se esconde detrás de
    // un genérico.
    [Fact]
    public async Task ADomainFailureKeepsItsOwnCode()
    {
        var harness = NewHarness(whatsAppFailure: new QuotationsDomainException(
            "order.whatsapp.recipient_missing", "The client has no phone number."));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Handler.HandleAsync(NewCommand(harness), TestContext.Current.CancellationToken));

        Assert.Equal("order.whatsapp.recipient_missing", error.Code);
    }

    // Lo demás sale con código propio y nombrando el paso, con la original adentro para que el log
    // de fallas de la API la guarde entera. Y como el mensaje no salió, no queda historial ni auditoría.
    [Fact]
    public async Task AnUnexpectedFailureBecomesAClearErrorThatNamesTheStage()
    {
        var harness = NewHarness(whatsAppFailure: new TaskCanceledException("A task was canceled."));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Handler.HandleAsync(NewCommand(harness), TestContext.Current.CancellationToken));

        Assert.Equal("order.send.failed", error.Code);
        Assert.Contains("WhatsApp", error.Message, StringComparison.Ordinal);
        Assert.IsType<TaskCanceledException>(error.InnerException);
        Assert.Empty(harness.Orders.History);
        Assert.Empty(harness.Audit.Actions);
    }

    private static SendOrderCommand NewCommand(
        Harness harness, string? recipient = null, Guid? connectionId = null, Guid? contactId = null, Guid? threadConnectionId = null) =>
        new(TenantId, harness.Order.Id.Value, recipient, connectionId, contactId, threadConnectionId);

    private static readonly Guid ContactId = Guid.CreateVersion7();

    private static readonly QuotationClientContact Contact = new(ContactId, "Laura Gómez", "3115550101");

    // Spec 2026-11-10 §4.6: a un contacto de la empresa del documento, con su teléfono y a su nombre.
    [Fact]
    public async Task ContactSendsToTheContactsPhoneAndName()
    {
        var harness = NewHarness(contacts: [Contact]);

        await harness.Handler.HandleAsync(NewCommand(harness, "Contact", contactId: ContactId), TestContext.Current.CancellationToken);

        Assert.NotNull(harness.Sender.SentOrder);
        Assert.Equal(
            ("3115550101", "Laura Gómez", (Guid?)ContactId),
            (harness.Sender.SentOrder.ToPhone, harness.Sender.SentOrder.FullName, harness.Sender.SentOrder.CustomerId));
    }

    // El id de un contacto de otra empresa, de otro tenant o fusionado, o el del propio cliente, es lo que el puerto no
    // devuelve: nada sale y nada queda (ni historial, ni auditoría, ni elección del canal).
    [Theory]
    [InlineData("anotherCompanyOrTenantOrMerged")]
    [InlineData("theDocumentsOwnClient")]
    [InlineData("noContactId")]
    public async Task AContactThatIsNotOfTheClientIsRejectedAndNothingIsWritten(string scenario)
    {
        var first = WhatsAppChannels.Any();
        var second = WhatsAppChannels.Meta();
        var harness = NewHarness(contacts: [Contact], channels: [first, second]);
        Guid? contactId = scenario switch
        {
            "theDocumentsOwnClient" => ClientId,
            "noContactId" => null,
            _ => Guid.CreateVersion7(),
        };

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => harness.Handler.HandleAsync(
            NewCommand(harness, "Contact", connectionId: second.ConnectionId, contactId: contactId),
            TestContext.Current.CancellationToken));

        Assert.Equal("order.order.contact_not_of_client", error.Code);
        Assert.Null(harness.Sender.SentOrder);
        Assert.Empty(harness.Orders.History);
        Assert.Empty(harness.Audit.Actions);
        Assert.Null(harness.Choices.Saved);
        Assert.Equal(0, harness.Choices.Remembered);
        Assert.Equal(0, harness.Renderer.Calls);
    }

    [Fact]
    public async Task ContactWithoutPhoneReachesTheSenderWithoutOneAndTheRouterAnswersRecipientMissing()
    {
        var noPhone = Contact with { Phone = null };
        var harness = NewHarness(contacts: [noPhone]);

        await harness.Handler.HandleAsync(NewCommand(harness, "Contact", contactId: ContactId), TestContext.Current.CancellationToken);
        Assert.Null(harness.Sender.SentOrder!.ToPhone);

        // La falta de teléfono la responde WhatsAppSenderRouter (WhatsAppSenderRouterTests), con el código de siempre.
        var failing = NewHarness(contacts: [noPhone], whatsAppFailure: new QuotationsDomainException(
            "order.whatsapp.recipient_missing", "The recipient has no phone number."));
        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            failing.Handler.HandleAsync(NewCommand(failing, "Contact", contactId: ContactId), TestContext.Current.CancellationToken));
        Assert.Equal("order.whatsapp.recipient_missing", error.Code);
        Assert.Empty(failing.Orders.History);
    }

    [Fact]
    public async Task EligibilityIsStillCheckedOnTheClient()
    {
        var harness = NewHarness(contacts: [Contact], customer: Customer with { TenantId = Guid.CreateVersion7() });

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Handler.HandleAsync(NewCommand(harness, "Contact", contactId: ContactId), TestContext.Current.CancellationToken));

        Assert.Equal("order.order.client_not_found", error.Code);
        Assert.Null(harness.Sender.SentOrder);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Customer")]
    [InlineData("Billing")]
    public async Task ContactIdAndThreadAreIgnoredWithoutTheContactRecipient(string? recipient)
    {
        var thread = WhatsAppChannels.Meta();
        var harness = NewHarness(billing: BillingParty, contacts: [Contact], channels: [WhatsAppChannels.Any(), thread]);

        // Con dos canales y ninguno elegido, un hilo ignorado deja el `channel_required` de siempre.
        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => harness.Handler.HandleAsync(
            NewCommand(harness, recipient, contactId: ContactId, threadConnectionId: thread.ConnectionId),
            TestContext.Current.CancellationToken));
        Assert.Equal("order.whatsapp.channel_required", error.Code);

        var single = NewHarness(billing: BillingParty, contacts: [Contact]);
        await single.Handler.HandleAsync(NewCommand(single, recipient, contactId: ContactId), TestContext.Current.CancellationToken);
        Assert.NotEqual(ContactId, single.Sender.SentOrder!.CustomerId);
        Assert.NotEqual("Laura Gómez", single.Sender.SentOrder.FullName);
        Assert.Equal(0, single.CustomerLookup.FindContactCalls);
    }

    [Fact]
    public async Task TheThreadConnectionDoesNotTouchTheSavedChoice()
    {
        var saved = WhatsAppChannels.Any();
        var thread = WhatsAppChannels.Meta();
        var harness = NewHarness(contacts: [Contact], channels: [saved, thread], saved: saved.ConnectionId);

        await harness.Handler.HandleAsync(
            NewCommand(harness, "Contact", contactId: ContactId, threadConnectionId: thread.ConnectionId),
            TestContext.Current.CancellationToken);

        Assert.Equal(thread.ConnectionId, harness.Sender.Channel?.ConnectionId);
        Assert.Equal(saved.ConnectionId, harness.Choices.Saved);
        Assert.Equal(0, harness.Choices.Remembered);
        Assert.DoesNotContain(ChooseWhatsAppChannelHandler.AuditAction, harness.Audit.Actions);
    }

    // La conexión del hilo cuenta sólo si puede mandar pedidos (plantilla de pedido aprobada): si no, rige la resolución de siempre.
    [Fact]
    public async Task AThreadConnectionWithoutTheOrderTemplateFallsBackToChannelRequired()
    {
        var withoutTemplate = WhatsAppChannels.Any(orderTemplateId: null);
        var harness = NewHarness(
            contacts: [Contact], channels: [withoutTemplate, WhatsAppChannels.Any(), WhatsAppChannels.Meta()]);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => harness.Handler.HandleAsync(
            NewCommand(harness, "Contact", contactId: ContactId, threadConnectionId: withoutTemplate.ConnectionId),
            TestContext.Current.CancellationToken));

        Assert.Equal("order.whatsapp.channel_required", error.Code);
        Assert.Null(harness.Sender.SentOrder);
    }

    [Fact]
    public async Task TheHistoryNamesTheContact()
    {
        var harness = NewHarness(contacts: [Contact]);

        await harness.Handler.HandleAsync(NewCommand(harness, "Contact", contactId: ContactId), TestContext.Current.CancellationToken);

        Assert.Equal("Enviado por WhatsApp a Laura Gómez.", Assert.Single(harness.Orders.History).Details);
    }

    // Plan 2026-10-21 (Zenvia por tenant).
    [Fact]
    public async Task SendsThroughTheOnlyAvailableChannel()
    {
        var channel = WhatsAppChannels.Any();
        var harness = NewHarness(channels: [channel]);

        await harness.Handler.HandleAsync(NewCommand(harness), TestContext.Current.CancellationToken);

        Assert.Equal(channel.ConnectionId, harness.Sender.Channel?.ConnectionId);
    }

    // Spec 2026-11-09 D6, D7 y D9: una conexión de Meta con plantilla de pedido es un canal más. Sola, el pedido sale por ella sin
    // publicar copia pública del PDF y con el tenant en el mensaje; junto a una Zenvia, hay que elegir.
    [Fact]
    public async Task OrderGoesThroughMetaWithoutPublishingThePdf()
    {
        var meta = WhatsAppChannels.Meta();
        var harness = NewHarness(channels: [meta]);

        await harness.Handler.HandleAsync(NewCommand(harness), TestContext.Current.CancellationToken);

        Assert.Equal(meta.ConnectionId, harness.Sender.Channel?.ConnectionId);
        var sent = harness.Sender.SentOrder;
        Assert.NotNull(sent);
        Assert.Null(sent.DocumentUrl);
        Assert.Null(harness.Storage.PublishedKey);
        Assert.NotNull(sent.DocumentStorageKey);
        Assert.Equal(TenantId, sent.TenantId);
        Assert.Equal([WhatsAppDocument.Order], harness.Channels.Documents);

        var both = NewHarness(channels: [WhatsAppChannels.Meta(), WhatsAppChannels.Any()]);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            both.Handler.HandleAsync(NewCommand(both), TestContext.Current.CancellationToken));

        Assert.Equal("order.whatsapp.channel_required", error.Code);
        Assert.Null(both.Sender.SentOrder);
    }

    // D7: la elección es la misma que la de cotización. Guardada y disponible, sale por ella sin preguntar; otro id es channel_already_set.
    [Fact]
    public async Task ASavedChoiceAvailableForOrdersIsUsedAndAnotherIdIsAlreadySet()
    {
        var chosen = WhatsAppChannels.Meta();
        var other = WhatsAppChannels.Any();
        var harness = NewHarness(channels: [chosen, other], saved: chosen.ConnectionId);

        await harness.Handler.HandleAsync(NewCommand(harness), TestContext.Current.CancellationToken);

        Assert.Equal(chosen.ConnectionId, harness.Sender.Channel?.ConnectionId);
        Assert.Equal(0, harness.Choices.Remembered);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Handler.HandleAsync(NewCommand(harness, connectionId: other.ConnectionId), TestContext.Current.CancellationToken));

        Assert.Equal("order.whatsapp.channel_already_set", error.Code);
    }

    // D7: una elección guardada que no sirve para pedidos (Zenvia sin orderTemplateId) se ignora y rigen las demás reglas.
    [Fact]
    public async Task ASavedChoiceNotAvailableForOrdersIsIgnored()
    {
        var saved = WhatsAppChannels.Any(orderTemplateId: null);
        var only = WhatsAppChannels.Meta();
        var harness = NewHarness(channels: [saved, only], saved: saved.ConnectionId);

        await harness.Handler.HandleAsync(NewCommand(harness), TestContext.Current.CancellationToken);

        Assert.Equal(only.ConnectionId, harness.Sender.Channel?.ConnectionId);
        Assert.Equal(0, harness.Choices.Remembered);
    }

    // D7: un envío de pedido con connectionId guarda la elección, con la misma auditoría que la cotización, y nunca si el envío falla.
    [Fact]
    public async Task AnOrderSendWithAConnectionIdRemembersTheChoiceUnlessTheSendFails()
    {
        var first = WhatsAppChannels.Meta();
        var second = WhatsAppChannels.Any();
        var harness = NewHarness(channels: [first, second]);

        await harness.Handler.HandleAsync(NewCommand(harness, connectionId: second.ConnectionId), TestContext.Current.CancellationToken);

        Assert.Equal(second.ConnectionId, harness.Sender.Channel?.ConnectionId);
        Assert.Equal((second.ConnectionId, 1), (harness.Choices.Saved, harness.Choices.Remembered));
        Assert.Equal(["quotation.order.sent", ChooseWhatsAppChannelHandler.AuditAction], harness.Audit.Actions);

        var failing = NewHarness(channels: [first, second], whatsAppFailure: new TaskCanceledException("A task was canceled."));

        await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            failing.Handler.HandleAsync(NewCommand(failing, connectionId: second.ConnectionId), TestContext.Current.CancellationToken));

        Assert.Null(failing.Choices.Saved);
        Assert.Empty(failing.Audit.Actions);
    }

    [Fact]
    public async Task AConnectionIdThatIsNotAvailableIsChannelUnavailable()
    {
        var harness = NewHarness(channels: [WhatsAppChannels.Meta()]);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Handler.HandleAsync(NewCommand(harness, connectionId: Guid.CreateVersion7()), TestContext.Current.CancellationToken));

        Assert.Equal("order.whatsapp.channel_unavailable", error.Code);
        Assert.Equal(0, harness.Renderer.Calls);
    }

    [Fact]
    public async Task TheOrderMessageCarriesTheStorageKeyTheMemberAndTheCustomer()
    {
        var harness = NewHarness();

        await harness.Handler.HandleAsync(NewCommand(harness), TestContext.Current.CancellationToken);

        var sent = harness.Sender.SentOrder;
        Assert.NotNull(sent);
        Assert.NotNull(sent.DocumentStorageKey);
        Assert.Equal(AdvisorId.Value, sent.SentByMemberId);
        Assert.Equal(ClientId, sent.CustomerId);
    }

    [Fact]
    public async Task NoChannelIsRejected()
    {
        var harness = NewHarness(channels: []);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Handler.HandleAsync(NewCommand(harness), TestContext.Current.CancellationToken));

        Assert.Equal("order.whatsapp.no_channel", error.Code);
        Assert.Equal(0, harness.Renderer.Calls);
        Assert.Null(harness.Sender.SentOrder);
    }

    [Fact]
    public async Task SeveralChannelsRequireAChoice()
    {
        var harness = NewHarness(channels: [WhatsAppChannels.Any(), WhatsAppChannels.Any()]);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Handler.HandleAsync(NewCommand(harness), TestContext.Current.CancellationToken));

        Assert.Equal("order.whatsapp.channel_required", error.Code);
        Assert.Equal(0, harness.Renderer.Calls);
        Assert.Null(harness.Sender.SentOrder);
    }

    // Spec 2026-11-09 D6: una Zenvia sin plantilla de pedido no está disponible para pedidos; sin otra, no_channel antes del PDF.
    [Fact]
    public async Task OrderChannelWithoutOrderTemplateIsNotAvailableAndIsNoChannelBeforeRendering()
    {
        var harness = NewHarness(channels: [WhatsAppChannels.Any(orderTemplateId: null)]);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Handler.HandleAsync(NewCommand(harness), TestContext.Current.CancellationToken));

        Assert.Equal("order.whatsapp.no_channel", error.Code);
        Assert.Equal(0, harness.Renderer.Calls);
        Assert.Null(harness.Sender.SentOrder);
    }

    // Revisión final: quien no es miembro activo es un 403 aunque el tenant tampoco tenga canal; el canal no
    // puede adelantarse a la pregunta de quién envía.
    [Fact]
    public async Task ANonMemberIsForbiddenEvenWithoutAChannel()
    {
        var harness = NewHarness(channels: [], membership: new NoMembershipDirectory());

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            harness.Handler.HandleAsync(NewCommand(harness), TestContext.Current.CancellationToken));
    }

    // El PDF del pedido imprime con los separadores del tenant (spec 2026-10-23, D-R3).
    [Fact]
    public async Task TheOrderDocumentUsesTheTenantSeparators()
    {
        var harness = NewHarness(tenantDirectory: StubTenantDirectory.WithNumberFormat(TenantNumberFormats.DotDecimal));

        await harness.Handler.HandleAsync(NewCommand(harness), TestContext.Current.CancellationToken);

        Assert.Equal(",", harness.Renderer.Last!.ThousandsSeparator);
        Assert.Equal(".", harness.Renderer.Last.DecimalSeparator);
    }

    [Fact]
    public async Task TheOrderMessageCarriesTheTenantNumberFormat()
    {
        var harness = NewHarness(tenantDirectory: StubTenantDirectory.WithNumberFormat(TenantNumberFormats.DotDecimal));

        await harness.Handler.HandleAsync(NewCommand(harness), TestContext.Current.CancellationToken);

        Assert.Equal(TenantNumberFormats.DotDecimal, harness.Sender.SentOrder!.NumberFormat);
    }

    private sealed record Harness(
        SendOrderHandler Handler,
        Order Order,
        StubOrderListRepository Orders,
        RecordingWhatsAppSender Sender,
        CountingPdfRenderer Renderer,
        RecordingPdfStorage Storage,
        RecordingQuotationAuditPublisher Audit,
        StubWhatsAppChannelDirectory Channels,
        InMemoryWhatsAppChannelChoiceStore Choices,
        StubQuotationCustomerLookup CustomerLookup);

    private static Harness NewHarness(
        QuotationPartyDetails? billing = null,
        bool cancelled = false,
        OrderStatus status = OrderStatus.Open,
        bool invoiced = false,
        QuotationCustomerRef? customer = null,
        Exception? whatsAppFailure = null,
        IExecutionContext? executionContext = null,
        WhatsAppChannel[]? channels = null,
        IMembershipDirectory? membership = null,
        StubTenantDirectory? tenantDirectory = null,
        Guid? saved = null,
        IEnumerable<QuotationClientContact>? contacts = null)
    {
        var tenant = tenantDirectory ?? new StubTenantDirectory();
        var directory = new StubWhatsAppChannelDirectory(channels ?? [WhatsAppChannels.Any()]);
        var choices = new InMemoryWhatsAppChannelChoiceStore(saved);
        var order = OrderBuilders.Pending(
            TenantId, AdvisorId, Now.AddDays(-1), clientId: ClientId,
            parties: billing is null
                ? null
                : new QuotationParties(billing, null, BillingVatSurplus: false));
        if (invoiced)
        {
            order.MarkPaid(AdvisorId, requirePaymentProof: false, Now.AddHours(-3));
            order.Invoice(AdvisorId, requireFullPayment: true, Now.AddHours(-2));
        }

        if (status is OrderStatus.Archived)
        {
            order.Archive(AdvisorId, Now.AddHours(-2));
        }

        if (cancelled)
        {
            order.Cancel(AdvisorId, "El cliente desistió", Now.AddHours(-1));
        }

        var orders = new StubOrderListRepository(order);
        var sender = new RecordingWhatsAppSender();
        var renderer = new CountingPdfRenderer();
        var storage = new RecordingPdfStorage("https://r2.example.com/ped.pdf?X-Amz-Signature=abc");
        var audit = new RecordingQuotationAuditPublisher();
        var customers = new StubQuotationCustomerLookup(customer ?? Customer);
        customers.Contacts.AddRange(contacts ?? []);

        var handler = new SendOrderHandler(
            orders,
            new StubQuotationRepository(AnyQuotation()),
            new NoOpQuotationsUnitOfWork(),
            audit,
            new OrderResponseComposer(
                customers,
                new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
                new StubQuotationProductLookup(new Dictionary<Guid, QuotationVariantRef>()),
                new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>())),
            new OrderPdfProvider(
                renderer, storage, new FixedTenantClock(Now), new StubQuotationLogoLookup(),
                tenant),
            storage,
            customers,
            whatsAppFailure is null ? sender : new FailingWhatsAppSender(whatsAppFailure),
            directory,
            choices,
            tenant,
            membership ?? new StubMembershipDirectory(AdvisorId.Value),
            executionContext ?? new StubExecutionContext(SubjectId, TenantId),
            new FixedClock(Now));

        return new Harness(handler, order, orders, sender, renderer, storage, audit, directory, choices, customers);
    }

    // El doble de IQuotationRepository exige una cotización; el pedido de estas pruebas es manual,
    // así que nunca se consulta.
    private static Quotation AnyQuotation() =>
        Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", ClientId, AdvisorId, new DateOnly(2026, 10, 30),
            paymentMethod: null, notes: null, QuotationParties.Empty, billingAccount: null, defaultCurrency: "COP",
            customerVatSurplus: false, customerWithholdings: [], AdvisorId, Now);
}
