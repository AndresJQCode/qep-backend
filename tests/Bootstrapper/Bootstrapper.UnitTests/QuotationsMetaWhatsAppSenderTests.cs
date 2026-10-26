using Modules.Messaging.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Storage.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-10-21 §4.1 y D16: la cotización sale por Mensajería con el PDF privado, la plantilla vigente de la conexión y
/// los valores ya formateados. Cualquier resultado distinto de enviado es <c>quotation.whatsapp.send_failed</c>.
/// </summary>
public sealed class QuotationsMetaWhatsAppSenderTests
{
    private const string StorageKey = "tenants/abc/quotations/COT-000123.pdf";

    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid MemberId = Guid.CreateVersion7();
    private static readonly Guid CustomerId = Guid.CreateVersion7();
    private static readonly byte[] Pdf = [0x25, 0x50, 0x44, 0x46, 0x2D];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SendsThePrivatePdfBytesTheTemplateAndTheFormattedValues()
    {
        var outbound = new RecordingOutbound();
        var storage = new StubStorage(StorageKey, Pdf);
        var channel = new MetaWhatsAppChannel(Guid.CreateVersion7(), "Cuenta de Meta", "+57 300 123 4567", "qep_cotizacion_v2", "es");
        var message = Message();

        await new QuotationsMetaWhatsAppSender(outbound, storage).SendQuotationAsync(channel, "573001234567", message, Ct);

        var send = Assert.Single(outbound.Sends);
        Assert.Equal(
            (TenantId, channel.ConnectionId, MemberId, "573001234567", (Guid?)CustomerId, "Ana Pérez"),
            (send.TenantId, send.ConnectionId, send.SentByMemberId, send.WaId, send.CustomerId, send.RecipientName));
        Assert.Equal(("qep_cotizacion_v2", "es"), (send.TemplateName, send.TemplateLanguage));
        Assert.Equal(("Ana Pérez", "COT-000123"), (send.FullName, send.OrderNumber));
        Assert.Equal(WhatsAppTemplateValues.Total(2450000m, "COP", null), send.Total);
        Assert.Equal(WhatsAppTemplateValues.ValidUntil(new DateOnly(2026, 9, 30)), send.ValidUntil);
        Assert.Equal("Cotizacion-COT-000123.pdf", send.DocumentFileName);
        Assert.Equal(Pdf, send.Document.ToArray());
    }

    [Theory]
    [MemberData(nameof(NotSentResults))]
    public async Task AnythingButSentIsSendFailed(QuotationTemplateSendResult result)
    {
        var sender = new QuotationsMetaWhatsAppSender(new RecordingOutbound(result), new StubStorage(StorageKey, Pdf));
        var channel = new MetaWhatsAppChannel(Guid.CreateVersion7(), "Cuenta de Meta", null, "qep_cotizacion", "es");

        var exception = await Assert.ThrowsAsync<QuotationsDomainException>(
            () => sender.SendQuotationAsync(channel, "573001234567", Message(), Ct));

        Assert.Equal("quotation.whatsapp.send_failed", exception.Code);
    }

    public static TheoryData<QuotationTemplateSendResult> NotSentResults() => new()
    {
        new QuotationTemplateSendResult.ConnectionUnavailable("credentials_rejected"),
        new QuotationTemplateSendResult.Failed("messaging.message.rejected"),
    };

    private static WhatsAppQuotationMessage Message() =>
        new(
            ToPhone: "300 123 4567",
            FullName: "Ana Pérez",
            OrderNumber: "COT-000123",
            Total: 2450000m,
            Currency: "COP",
            ValidUntil: new DateOnly(2026, 9, 30),
            DocumentUrl: null,
            DocumentStorageKey: StorageKey,
            SentByMemberId: MemberId,
            CustomerId: CustomerId,
            TenantId: TenantId);

    private sealed class RecordingOutbound(QuotationTemplateSendResult? result = null) : IQuotationTemplateOutbound
    {
        public List<QuotationTemplateSend> Sends { get; } = [];

        public Task<QuotationTemplateSendResult> SendAsync(QuotationTemplateSend send, CancellationToken cancellationToken)
        {
            Sends.Add(send);
            return Task.FromResult(result ?? new QuotationTemplateSendResult.Sent(Guid.CreateVersion7(), Guid.CreateVersion7()));
        }
    }

    private sealed class StubStorage(string key, byte[] content) : IObjectStorage
    {
        public Task<byte[]> DownloadAsync(string requested, CancellationToken cancellationToken) =>
            requested == key ? Task.FromResult(content) : throw new InvalidOperationException($"Unexpected key '{requested}'.");

        public Task<Uri> CreatePresignedUploadUrlAsync(string key, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Uri> CreatePresignedDownloadUrlAsync(string key, string? downloadFileName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Uri> CreatePresignedDownloadUrlAsync(string key, TimeSpan expiry, string? downloadFileName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StoredObject?> StatAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task PromoteAsync(string sourceKey, string destinationKey, string expectedChecksum, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UploadAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UploadAsync(string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StoredObjectStream?> OpenReadAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
