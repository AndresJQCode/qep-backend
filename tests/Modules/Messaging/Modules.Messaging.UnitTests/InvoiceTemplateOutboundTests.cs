using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-11-21: el envío de la factura por Meta es el de <see cref="DocumentTemplateOutbound"/> con las claves de la factura.
/// El procedimiento completo (conversación, reclamo, Meta) lo cubre <c>QuotationTemplateOutboundTests</c> y el de la factura, por
/// integración; acá se prueba lo propio de la factura: qué valor va en qué clave y cómo se traduce el resultado del núcleo.</summary>
public sealed class InvoiceTemplateOutboundTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ApprovedTemplate Template = new(
        "qep_factura", "es", "UTILITY", "NAMED",
        InvoiceTemplateDefinition.Payload("4::handle", "qep_factura", InvoiceTemplateDefinition.BaseBody)["components"]!.ToJsonString());

    private static InvoiceTemplateSend Send(string waId = "573001234567") =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), waId, null, "Laura Gómez", Template,
            "Laura Gómez", "FV-1-000123", "2.450.000 COP", new byte[] { 1, 2, 3 }, "Factura-FV-1-000123.pdf");

    [Fact]
    public void TheValuesAreContactNameInvoiceNumberAndTotal()
    {
        var send = Send();

        var forwarded = InvoiceTemplateOutbound.ToDocumentSend(send);

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["nombre_contacto"] = "Laura Gómez",
                ["numero_factura"] = "FV-1-000123",
                ["valor_total"] = "2.450.000 COP",
            },
            forwarded.Values.ToDictionary(pair => pair.Key, pair => pair.Value));
        Assert.Equal(
            (send.TenantId, send.ConnectionId, send.SentByMemberId, send.WaId, send.CustomerId, send.RecipientName, send.DocumentFileName),
            (forwarded.TenantId, forwarded.ConnectionId, forwarded.SentByMemberId, forwarded.WaId, forwarded.CustomerId, forwarded.RecipientName, forwarded.DocumentFileName));
        Assert.Same(Template, forwarded.Template);
        Assert.Equal(send.Document.ToArray(), forwarded.Document.ToArray());
        // Las del remitente las agrega TemplateSenderValues si la fila las usa; la factura no manda las del pedido ni las de la cotización.
        Assert.DoesNotContain("numero_pedido", forwarded.Values.Keys);
        Assert.DoesNotContain("vigencia", forwarded.Values.Keys);
    }

    [Fact]
    public void AFailedSendMapsToTheInvoiceResult()
    {
        var messageId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();

        Assert.Equal(new InvoiceTemplateSendResult.Sent(messageId, conversationId), InvoiceTemplateOutbound.Map(new QuotationTemplateSendResult.Sent(messageId, conversationId)));
        Assert.Equal(new InvoiceTemplateSendResult.ConnectionUnavailable("paused"), InvoiceTemplateOutbound.Map(new QuotationTemplateSendResult.ConnectionUnavailable("paused")));
        Assert.Equal(new InvoiceTemplateSendResult.Failed("messaging.message.rejected"), InvoiceTemplateOutbound.Map(new QuotationTemplateSendResult.Failed("messaging.message.rejected")));
    }

    // Por el objeto real: un teléfono que no son sólo dígitos se rechaza antes de tocar la conexión, el cliente o el PDF.
    [Fact]
    public async Task APhoneThatIsNotDigitsIsRejectedBeforeAnyCall()
    {
        var bed = new MessagingTestBed();
        var sut = new InvoiceTemplateOutbound(
            bed.Conversations, bed.Repository, bed.Outbound, bed.Meta, bed.Connections, bed.Notifier, bed.Receipts, new FakeClock(bed.Now),
            new FakeConversationOpener(bed), bed.Store, new TemplateSenderValues(new NoMembers(), new NoTenants(), new NoClock()));

        var result = await sut.SendAsync(Send("+57 300 123"), Ct);

        Assert.Equal(new InvoiceTemplateSendResult.Failed(MessagingErrorCodes.MessageRejected), result);
        Assert.Empty(bed.Meta.TemplateSends);
        Assert.Empty(bed.Store.Objects);
    }

    private sealed class NoMembers : IMessagingMemberNames
    {
        public Task<IReadOnlyDictionary<Guid, string>> FindAsync(Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not expected.");

        public Task<string?> FindDisplayNameAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not expected.");
    }

    private sealed class NoTenants : IMessagingTenantNames
    {
        public Task<string?> FindAsync(Guid tenantId, CancellationToken cancellationToken) => throw new InvalidOperationException("Not expected.");
    }

    private sealed class NoClock : ITenantClock
    {
        public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken) => throw new InvalidOperationException("Not expected.");
    }
}
