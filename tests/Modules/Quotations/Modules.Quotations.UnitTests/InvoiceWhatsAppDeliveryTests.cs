using BuildingBlocks.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// La fachada con la que Siigo manda la factura por WhatsApp (spec 2026-11-21): el orden de las
/// comprobaciones, la copia pública sólo para Zenvia, y que mandar nunca cambie la elección de canal
/// del tenant.
/// </summary>
public sealed class InvoiceWhatsAppDeliveryTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 15, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Pdf = [0x25, 0x50, 0x44, 0x46];

    private static readonly QuotationCustomerRef Customer = new(
        ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
        "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false);

    [Fact]
    public async Task ASuccessfulMetaSendCarriesTheBytesAndNoUrl()
    {
        var harness = NewHarness(channels: [WhatsAppChannels.Meta()]);

        await harness.Delivery.SendAsync(harness.Request(), TestContext.Current.CancellationToken);

        var sent = harness.Sender.SentInvoice;
        Assert.NotNull(sent);
        Assert.Equal(Pdf, sent.Document);
        Assert.Null(sent.DocumentUrl);
        Assert.Equal(
            ("FV-1-123", "COP", "3001234567", "Ferretería El Tornillo", ClientId, AdvisorId.Value, TenantId),
            (sent.InvoiceNumber, sent.Currency, sent.ToPhone, sent.FullName, sent.CustomerId, sent.SentByMemberId, sent.TenantId));
    }

    [Fact]
    public async Task ASuccessfulZenviaSendCarriesTheUrlAndPublishesOnce()
    {
        var harness = NewHarness(channels: [WhatsAppChannels.Any()]);

        await harness.Delivery.SendAsync(harness.Request(), TestContext.Current.CancellationToken);

        var sent = harness.Sender.SentInvoice;
        Assert.NotNull(sent);
        Assert.Equal(RecordingPdfStorage.InvoicePublicUrl, sent.DocumentUrl);
        Assert.Null(sent.Document);
        Assert.Equal(Pdf, Assert.Single(harness.Storage.PublishedInvoices));
        Assert.IsType<ZenviaWhatsAppChannel>(harness.Sender.Channel);
    }

    // Es la copia pública de Quotations (`IQuotationPdfStorage`); el hilo de Mensajería guarda su propia copia privada del adjunto de Meta.
    [Fact]
    public async Task MetaNeverPublishesACopy()
    {
        var harness = NewHarness(channels: [WhatsAppChannels.Meta()]);

        await harness.Delivery.SendAsync(harness.Request(), TestContext.Current.CancellationToken);

        Assert.Empty(harness.Storage.PublishedInvoices);
        Assert.Null(harness.Storage.PublishedKey);
    }

    // Sin teléfono no se gasta nada: ni se pregunta por canales, ni se publica el PDF.
    [Fact]
    public async Task ACustomerWithoutPhoneFailsBeforeAnyChannelWork()
    {
        var harness = NewHarness(customer: Customer with { Phone = null });

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Delivery.SendAsync(harness.Request(), TestContext.Current.CancellationToken));

        Assert.Equal("invoice.whatsapp.no_phone", error.Code);
        Assert.Empty(harness.Channels.Documents);
        Assert.Empty(harness.Storage.PublishedInvoices);
        Assert.Null(harness.Sender.SentInvoice);
    }

    [Fact]
    public async Task NoChannelFailsWithInvoiceNoChannel()
    {
        var harness = NewHarness(channels: []);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Delivery.SendAsync(harness.Request(), TestContext.Current.CancellationToken));

        Assert.Equal("invoice.whatsapp.no_channel", error.Code);
        Assert.Equal([WhatsAppDocument.Invoice], harness.Channels.Documents);
    }

    // La regla del documento: una Zenvia sin plantilla de factura no cuenta como canal para una factura.
    [Fact]
    public async Task AZenviaWithoutAnInvoiceTemplateIsNotAChannel()
    {
        var harness = NewHarness(channels: [WhatsAppChannels.Any(invoiceTemplateId: null)]);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Delivery.SendAsync(harness.Request(), TestContext.Current.CancellationToken));

        Assert.Equal("invoice.whatsapp.no_channel", error.Code);
    }

    [Fact]
    public async Task TwoChannelsWithoutAChoiceFailWithChannelRequired()
    {
        var harness = NewHarness(channels: [WhatsAppChannels.Any(), WhatsAppChannels.Meta()]);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Delivery.SendAsync(harness.Request(), TestContext.Current.CancellationToken));

        Assert.Equal("invoice.whatsapp.channel_required", error.Code);
        Assert.Empty(harness.Storage.PublishedInvoices);
    }

    [Fact]
    public async Task TheSavedChoiceIsUsedAndNotRewritten()
    {
        var zenvia = WhatsAppChannels.Any();
        var meta = WhatsAppChannels.Meta();
        var harness = NewHarness(channels: [zenvia, meta], saved: meta.ConnectionId);

        await harness.Delivery.SendAsync(harness.Request(), TestContext.Current.CancellationToken);

        Assert.Equal(meta.ConnectionId, harness.Sender.Channel!.ConnectionId);
        Assert.Equal(0, harness.Choices.Remembered);
        Assert.Equal(meta.ConnectionId, harness.Choices.Saved);
    }

    [Fact]
    public async Task TheTotalFallsBackToTheOrderTotalWhenTheInvoiceHasNone()
    {
        var harness = NewHarness();

        await harness.Delivery.SendAsync(
            harness.Request(invoiceTotal: null), TestContext.Current.CancellationToken);

        Assert.Equal(harness.Order.Total, harness.Sender.SentInvoice!.Total);
    }

    [Fact]
    public async Task TheInvoiceTotalWinsOverTheOrderTotal()
    {
        var harness = NewHarness();

        await harness.Delivery.SendAsync(
            harness.Request(invoiceTotal: 1234.5m), TestContext.Current.CancellationToken);

        Assert.Equal(1234.5m, harness.Sender.SentInvoice!.Total);
    }

    [Fact]
    public async Task AnOrderOfAnotherTenantIsNotFound()
    {
        var harness = NewHarness();

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            harness.Delivery.SendAsync(
                harness.Request() with { OrderId = Guid.CreateVersion7() }, TestContext.Current.CancellationToken));

        Assert.Equal("order.order.not_found", error.Code);
        Assert.Empty(harness.Channels.Documents);
    }

    [Fact]
    public async Task ASendFailureIsRethrownAsItComes()
    {
        var failure = new QuotationsDomainException("invoice.whatsapp.send_failed", "boom");
        var harness = NewHarness(whatsAppFailure: failure);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Delivery.SendAsync(harness.Request(), TestContext.Current.CancellationToken));

        Assert.Same(failure, error);
    }

    [Fact]
    public async Task CanSendIsTrueWithAChannelAndAPhone()
    {
        var harness = NewHarness();

        Assert.True(await harness.Delivery.CanSendAsync(
            TenantId, harness.Order.Id.Value, TestContext.Current.CancellationToken));
        Assert.Equal([WhatsAppDocument.Invoice], harness.Channels.Documents);
    }

    [Fact]
    public async Task CanSendIsFalseWithoutChannelOrPhone()
    {
        var withoutChannel = NewHarness(channels: []);
        var withoutPhone = NewHarness(customer: Customer with { Phone = " " });

        Assert.False(await withoutChannel.Delivery.CanSendAsync(
            TenantId, withoutChannel.Order.Id.Value, TestContext.Current.CancellationToken));
        Assert.False(await withoutPhone.Delivery.CanSendAsync(
            TenantId, withoutPhone.Order.Id.Value, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CanSendIsFalseWhenTwoChannelsHaveNoSavedChoice()
    {
        var harness = NewHarness(channels: [WhatsAppChannels.Any(), WhatsAppChannels.Meta()]);

        Assert.False(await harness.Delivery.CanSendAsync(
            TenantId, harness.Order.Id.Value, TestContext.Current.CancellationToken));
        Assert.Equal(0, harness.Choices.Remembered);
    }

    [Fact]
    public async Task CanSendIsTrueWithTwoChannelsAndASavedChoice()
    {
        var meta = WhatsAppChannels.Meta();
        var harness = NewHarness(channels: [WhatsAppChannels.Any(), meta], saved: meta.ConnectionId);

        Assert.True(await harness.Delivery.CanSendAsync(
            TenantId, harness.Order.Id.Value, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CanSendIsTrueWithASingleChannelAndNoSavedChoice()
    {
        var harness = NewHarness(channels: [WhatsAppChannels.Meta()]);

        Assert.True(await harness.Delivery.CanSendAsync(
            TenantId, harness.Order.Id.Value, TestContext.Current.CancellationToken));
    }

    // Si la lectura regional falla, no se debe haber publicado la copia pública de Zenvia (quedaría huérfana).
    [Fact]
    public async Task ARegionalLookupFailurePublishesNothing()
    {
        var failure = new InvalidOperationException("regional down");
        var harness = NewHarness(channels: [WhatsAppChannels.Any()], regionalFailure: failure);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Delivery.SendAsync(harness.Request(), TestContext.Current.CancellationToken));

        Assert.Same(failure, error);
        Assert.Empty(harness.Storage.PublishedInvoices);
        Assert.Null(harness.Sender.SentInvoice);
    }

    [Fact]
    public async Task APublishFailureBecomesSendFailedNamingTheStepAndKeepsTheInnerException()
    {
        var inner = new InvalidOperationException("Public bucket is not configured: https://secret.example/x 3001234567");
        var harness = NewHarness(channels: [WhatsAppChannels.Any()]);
        harness.Storage.InvoicePublishFailure = inner;

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Delivery.SendAsync(harness.Request(), TestContext.Current.CancellationToken));

        Assert.Equal("invoice.whatsapp.send_failed", error.Code);
        Assert.Same(inner, error.InnerException);
        Assert.Contains("publish", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("3001234567", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.example", error.Message, StringComparison.Ordinal);
        Assert.Null(harness.Sender.SentInvoice);
    }

    [Fact]
    public async Task ANonDomainSendFailureBecomesSendFailedNamingTheStep()
    {
        var inner = new HttpRequestException("timeout calling zenvia with 3001234567");
        var harness = NewHarness(whatsAppFailure: inner);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Delivery.SendAsync(harness.Request(), TestContext.Current.CancellationToken));

        Assert.Equal("invoice.whatsapp.send_failed", error.Code);
        Assert.Same(inner, error.InnerException);
        Assert.Contains("send", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("3001234567", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelledSendStillPropagates()
    {
        var harness = NewHarness(whatsAppFailure: new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            harness.Delivery.SendAsync(harness.Request(), TestContext.Current.CancellationToken));
    }

    // Un teléfono con texto pero sin dígitos no es un destinatario: se rechaza antes de publicar la copia y no se ofrece el botón.
    [Fact]
    public async Task APhoneThatCannotBeNormalizedFailsWithNoPhoneBeforePublishing()
    {
        var harness = NewHarness(customer: Customer with { Phone = "sin telefono" }, channels: [WhatsAppChannels.Any()]);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            harness.Delivery.SendAsync(harness.Request(), TestContext.Current.CancellationToken));

        Assert.Equal("invoice.whatsapp.no_phone", error.Code);
        Assert.Empty(harness.Storage.PublishedInvoices);
        Assert.Empty(harness.Channels.Documents);
    }

    [Fact]
    public async Task CanSendIsFalseWhenThePhoneCannotBeNormalized()
    {
        var harness = NewHarness(customer: Customer with { Phone = "n/a" });

        Assert.False(await harness.Delivery.CanSendAsync(
            TenantId, harness.Order.Id.Value, TestContext.Current.CancellationToken));
    }

    private sealed record Harness(
        InvoiceWhatsAppDelivery Delivery,
        Order Order,
        RecordingWhatsAppSender Sender,
        RecordingPdfStorage Storage,
        StubWhatsAppChannelDirectory Channels,
        InMemoryWhatsAppChannelChoiceStore Choices)
    {
        public InvoiceWhatsAppRequest Request(decimal? invoiceTotal = 2450000m) =>
            new(TenantId, Order.Id.Value, "FV-1-123", invoiceTotal, Pdf);
    }

    private static Harness NewHarness(
        QuotationCustomerRef? customer = null,
        WhatsAppChannel[]? channels = null,
        Guid? saved = null,
        Exception? whatsAppFailure = null,
        Exception? regionalFailure = null)
    {
        var directory = new StubWhatsAppChannelDirectory(channels ?? [WhatsAppChannels.Any()]);
        var choices = new InMemoryWhatsAppChannelChoiceStore(saved);
        var order = OrderBuilders.Pending(TenantId, AdvisorId, Now.AddDays(-1), clientId: ClientId);
        var sender = new RecordingWhatsAppSender();
        var storage = new RecordingPdfStorage("https://r2.example.com/x.pdf?X-Amz-Signature=abc");

        var delivery = new InvoiceWhatsAppDelivery(
            new StubOrderListRepository(order),
            new StubQuotationCustomerLookup(customer ?? Customer),
            directory,
            choices,
            new StubTenantDirectory { RegionalFailure = regionalFailure },
            new StubMembershipDirectory(AdvisorId.Value),
            new StubExecutionContext(SubjectId, TenantId),
            whatsAppFailure is null ? sender : new FailingWhatsAppSender(whatsAppFailure),
            storage);

        return new Harness(delivery, order, sender, storage, directory, choices);
    }
}
