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

    // Enviar no es una transición: pendiente, aprobado y facturado se mandan igual.
    [Theory]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.Approved)]
    [InlineData(OrderStatus.Invoiced)]
    public async Task AnOrderThatIsNotCancelledCanBeSent(OrderStatus status)
    {
        var harness = NewHarness(status: status);

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

    private static SendOrderCommand NewCommand(Harness harness, string? recipient = null) =>
        new(TenantId, harness.Order.Id.Value, recipient);

    // Plan 2026-10-21 (Zenvia por tenant).
    [Fact]
    public async Task SendsThroughTheOnlyAvailableChannel()
    {
        var channel = WhatsAppChannels.Any();
        var harness = NewHarness(channels: [channel]);

        await harness.Handler.HandleAsync(NewCommand(harness), TestContext.Current.CancellationToken);

        Assert.Equal(channel.ConnectionId, harness.Sender.Channel?.ConnectionId);
    }

    // Spec 2026-10-21 D12: los pedidos salen sólo por Zenvia; una conexión de Meta no cuenta como canal.
    [Fact]
    public async Task OrderIgnoresMetaChannels()
    {
        var zenvia = WhatsAppChannels.Any();
        var harness = NewHarness(channels: [WhatsAppChannels.Meta(), zenvia]);

        await harness.Handler.HandleAsync(NewCommand(harness), TestContext.Current.CancellationToken);

        Assert.Equal(zenvia.ConnectionId, harness.Sender.Channel?.ConnectionId);

        var onlyMeta = NewHarness(channels: [WhatsAppChannels.Meta()]);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            onlyMeta.Handler.HandleAsync(NewCommand(onlyMeta), TestContext.Current.CancellationToken));

        Assert.Equal("order.whatsapp.no_channel", error.Code);
        Assert.Null(onlyMeta.Sender.SentOrder);
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

    // R4 y Review Focus 4: sin plantilla de pedido no sale ningún pedido, y saberlo no cuesta un PDF.
    [Fact]
    public async Task OrderChannelWithoutOrderTemplateIsRejectedBeforeRendering()
    {
        var harness = NewHarness(channels: [WhatsAppChannels.Any(orderTemplateId: null)]);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Handler.HandleAsync(NewCommand(harness), TestContext.Current.CancellationToken));

        Assert.Equal("order.whatsapp.not_configured", error.Code);
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
        RecordingQuotationAuditPublisher Audit);

    private static Harness NewHarness(
        QuotationPartyDetails? billing = null,
        bool cancelled = false,
        OrderStatus status = OrderStatus.Pending,
        QuotationCustomerRef? customer = null,
        Exception? whatsAppFailure = null,
        IExecutionContext? executionContext = null,
        WhatsAppChannel[]? channels = null,
        IMembershipDirectory? membership = null,
        StubTenantDirectory? tenantDirectory = null)
    {
        var tenant = tenantDirectory ?? new StubTenantDirectory();
        var order = OrderBuilders.Pending(
            TenantId, AdvisorId, Now.AddDays(-1), clientId: ClientId,
            parties: billing is null
                ? null
                : new QuotationParties(billing, null, BillingWithRetention: false, BillingVatSurplus: false));
        if (status is OrderStatus.Approved or OrderStatus.Invoiced)
        {
            order.Approve(AdvisorId, Now.AddHours(-3));
        }

        if (status is OrderStatus.Invoiced)
        {
            order.Invoice(AdvisorId, Now.AddHours(-2));
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
            new StubWhatsAppChannelDirectory(channels ?? [WhatsAppChannels.Any()]),
            tenant,
            membership ?? new StubMembershipDirectory(AdvisorId.Value),
            executionContext ?? new StubExecutionContext(SubjectId, TenantId),
            new FixedClock(Now));

        return new Harness(handler, order, orders, sender, renderer, storage, audit);
    }

    // El doble de IQuotationRepository exige una cotización; el pedido de estas pruebas es manual,
    // así que nunca se consulta.
    private static Quotation AnyQuotation() =>
        Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", ClientId, AdvisorId, new DateOnly(2026, 10, 30),
            paymentMethod: null, notes: null, QuotationParties.Empty, billingAccount: null, defaultCurrency: "COP",
            customerWithRetention: false, customerVatSurplus: false, AdvisorId, Now);
}
