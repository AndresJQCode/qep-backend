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

    [Fact]
    public async Task SendHandsTheSenderThePresignedPdfUrl()
    {
        var (handler, sender, _) = NewHandler();

        await handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken);

        Assert.NotNull(sender.Sent);
        Assert.Equal(PresignedUrl, sender.Sent.DocumentUrl);
    }

    // El destinatario recibe el archivo con este nombre, no con la clave de almacenamiento.
    [Fact]
    public async Task SendAsksForTheFileUnderAReadableName()
    {
        var (handler, _, files) = NewHandler();

        await handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken);

        Assert.Equal("Cotizacion-QUO-2026-0001.pdf", files.RequestedFileName);
    }

    [Fact]
    public async Task SendHandsTheSenderTheQuotationTotalAndValidity()
    {
        var (handler, sender, _) = NewHandler();

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
        var (handler, sender, files) = NewHandler(withValidUntil: false);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            handler.HandleAsync(NewCommand(), TestContext.Current.CancellationToken));

        Assert.Equal("quotation.quotation.valid_until_required", error.Code);
        Assert.Null(sender.Sent);
        Assert.Null(files.RequestedFileName);
    }

    private static SendQuotationCommand NewCommand() =>
        new(TenantId, Guid.CreateVersion7(), Guid.CreateVersion7());

    private static (
        SendQuotationHandler Handler,
        RecordingWhatsAppSender Sender,
        StubQuotationFileLookup Files) NewHandler(bool withValidUntil = true)
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

        var sender = new RecordingWhatsAppSender();
        var files = new StubQuotationFileLookup(PresignedUrl);
        var customer = new QuotationCustomerRef(
            ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
            "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false);

        var handler = new SendQuotationHandler(
            new StubQuotationRepository(quotation),
            new NoOpQuotationsUnitOfWork(),
            new NoOpQuotationAuditPublisher(),
            files,
            new StubQuotationCustomerLookup(customer),
            sender,
            new StubMembershipDirectory(AdvisorId.Value),
            new StubExecutionContext(SubjectId, TenantId),
            new FixedClock(Now));

        return (handler, sender, files);
    }
}
