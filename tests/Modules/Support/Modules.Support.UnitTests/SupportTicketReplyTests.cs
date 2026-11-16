using System.Text.Json;
using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

// Una respuesta del ticket (spec §8.3, plan E16): nace Pending, sale Sent o Failed una sola vez, y el ticket la anota en su traza.
public sealed class SupportTicketReplyTests
{
    private static readonly Guid TenantId = Guid.Parse("0193a000-0000-7000-8000-000000000001");
    private static readonly Guid Actor = Guid.Parse("0193a000-0000-7000-8000-0000000000a1");
    private static readonly Guid ConversationId = Guid.Parse("0193a000-0000-7000-8000-0000000000d1");
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    private static readonly SupportClockContext Clock =
        new(FakeCalendar.WithHolidays, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    private static SupportTicket Ticket() =>
        SupportTicket.CreateFromConversation(
            SupportTicketId.New(), TenantId, 1, SupportTicketTypeDefaults.For(TenantId, Now)[0], "Asunto", "Descripción",
            new SupportConversationRef(ConversationId, "WhatsApp"), null,
            new TicketRequester("Ana", null, null, null, null, null, null, null, "CO", null), null, Actor, Now, Clock);

    private static SupportTicketReply Reply(SupportTicket ticket) =>
        SupportTicketReply.Start(
            TenantId, ticket.Id, Guid.CreateVersion7(), SupportReplyChannel.Thread, SupportReplyDelivery.Template,
            "  Ya revisamos tu caso.  ", connectionId: null, Actor, Now.AddMinutes(1));

    [Fact]
    public void AReplyMovesOnlyFromPending()
    {
        var reply = Reply(Ticket());

        Assert.Equal(SupportReplyStatus.Pending, reply.Status);
        Assert.Equal("Ya revisamos tu caso.", reply.Text);

        reply.MarkFailed("send_failed", messageId: null, Now.AddMinutes(2));

        Assert.Equal(SupportReplyStatus.Failed, reply.Status);
        Assert.Equal("send_failed", reply.FailureCode);
        Assert.Throws<InvalidOperationException>(() => reply.MarkSent(Guid.CreateVersion7(), Now.AddMinutes(3)));
        Assert.Throws<InvalidOperationException>(() => reply.MarkFailed("send_failed", messageId: null, Now.AddMinutes(3)));
    }

    // Revisión final (M4): el mensaje que Mensajería registró como fallido queda en la fila, para que la pantalla lo encuentre en el hilo.
    [Fact]
    public void AFailedReplyKeepsTheMessageMessagingRecorded()
    {
        var reply = Reply(Ticket());
        var messageId = Guid.CreateVersion7();

        reply.MarkFailed("send_failed", messageId, Now.AddMinutes(2));

        Assert.Equal((SupportReplyStatus.Failed, (Guid?)messageId), (reply.Status, reply.MessageId));
    }

    // Revisión final (I1 y M2): la fila guarda a dónde y qué sale (el wa_id de la plantilla al celular, el adjunto del hilo), para que
    // retomarla mande exactamente lo guardado.
    [Fact]
    public void AReplyStoresItsTemplateTargetAndItsAttachment()
    {
        var connection = Guid.CreateVersion7();
        var upload = Guid.CreateVersion7();

        var template = SupportTicketReply.Start(
            TenantId, SupportTicketId.New(), Guid.CreateVersion7(), SupportReplyChannel.WhatsAppTemplate, SupportReplyDelivery.Template,
            "Hola", connection, Actor, Now, targetWaId: "573001234567");
        var attached = SupportTicketReply.Start(
            TenantId, SupportTicketId.New(), Guid.CreateVersion7(), SupportReplyChannel.Thread, SupportReplyDelivery.Text,
            "Te adjunto la factura", null, Actor, Now, uploadId: upload, fileName: "  factura.pdf ");

        Assert.Equal(((Guid?)connection, "573001234567"), (template.ConnectionId, template.TargetWaId));
        Assert.Equal(((Guid?)upload, "factura.pdf"), (attached.UploadId, attached.FileName));
    }

    // Owner 2026-11-15: «Reintentar» no puede reenviar como texto una respuesta que llevaba archivo, así que la respuesta dice si lo
    // llevaba. Sale de la fila (el adjunto guardado), también para una fallida.
    [Fact]
    public void TheReplyDtoSaysWhetherTheReplyCarriedAnAttachment()
    {
        var attached = SupportTicketReply.Start(
            TenantId, SupportTicketId.New(), Guid.CreateVersion7(), SupportReplyChannel.Thread, SupportReplyDelivery.Text,
            "Te adjunto la factura", null, Actor, Now, uploadId: Guid.CreateVersion7(), fileName: "factura.pdf");
        attached.MarkFailed("send_failed", messageId: null, Now.AddMinutes(1));
        var textOnly = SupportTicketReply.Start(
            TenantId, SupportTicketId.New(), Guid.CreateVersion7(), SupportReplyChannel.Thread, SupportReplyDelivery.Text,
            "Hola", null, Actor, Now);
        var members = new Dictionary<Guid, string>();

        Assert.True(SupportTicketDtoComposer.ToReplyDto(attached, members).HasAttachment);
        Assert.False(SupportTicketDtoComposer.ToReplyDto(textOnly, members).HasAttachment);
    }

    [Fact]
    public void ATargetOrAnAttachmentOutsideItsDeliveryIsRejected()
    {
        Assert.Throws<ArgumentException>(() => SupportTicketReply.Start(
            TenantId, SupportTicketId.New(), Guid.CreateVersion7(), SupportReplyChannel.Thread, SupportReplyDelivery.Text,
            "Hola", null, Actor, Now, targetWaId: "573001234567"));
        Assert.Throws<ArgumentException>(() => SupportTicketReply.Start(
            TenantId, SupportTicketId.New(), Guid.CreateVersion7(), SupportReplyChannel.Thread, SupportReplyDelivery.Template,
            "Hola", null, Actor, Now, uploadId: Guid.CreateVersion7(), fileName: "factura.pdf"));
        Assert.Throws<ArgumentException>(() => SupportTicketReply.Start(
            TenantId, SupportTicketId.New(), Guid.CreateVersion7(), SupportReplyChannel.Thread, SupportReplyDelivery.Text,
            "Hola", null, Actor, Now, uploadId: Guid.CreateVersion7()));
        Assert.Throws<ArgumentException>(() => SupportTicketReply.Start(
            TenantId, SupportTicketId.New(), Guid.CreateVersion7(), SupportReplyChannel.WhatsAppTemplate, SupportReplyDelivery.Template,
            "Hola", null, Actor, Now, targetWaId: new string('5', SupportTicketReply.TargetWaIdMaxLength + 1)));
    }

    [Fact]
    public void RecordingAReplyWritesReplySentWithChannelAndMessage()
    {
        var ticket = Ticket();
        var reply = Reply(ticket);
        var messageId = Guid.CreateVersion7();
        reply.MarkSent(messageId, Now.AddMinutes(2));

        ticket.RecordReply(reply, Now.AddMinutes(2));

        var sent = ticket.Events[^1];
        Assert.Equal(SupportTicketEventKind.ReplySent, sent.Kind);
        Assert.Equal(Actor, sent.ActorMemberId);
        Assert.Equal(2L, ticket.Version);
        var details = JsonDocument.Parse(sent.Details).RootElement;
        Assert.Equal(reply.Id, details.GetProperty("replyId").GetGuid());
        Assert.Equal("Thread", details.GetProperty("channel").GetString());
        Assert.Equal("Template", details.GetProperty("delivery").GetString());
        Assert.Equal(messageId, details.GetProperty("messageId").GetGuid());
        Assert.Equal("Sent", details.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, details.GetProperty("failureCode").ValueKind);
    }

    // Revisión del bloque A (AM1): la regla de texto de SendMessageValidator, repetida en el dominio.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hola\u0007")]
    [InlineData("4097")]
    public void ATextOutsideTheRuleIsRejected(string text)
    {
        var value = text == "4097" ? new string('a', SupportTicketReply.TextMaxLength + 1) : text;

        Assert.False(SupportTicketReply.IsValidText(value));
        Assert.Throws<ArgumentException>(() => SupportTicketReply.Start(
            TenantId, SupportTicketId.New(), Guid.CreateVersion7(), SupportReplyChannel.Thread, SupportReplyDelivery.Text, value, null, Actor, Now));
    }

    [Theory]
    [InlineData("Línea uno\nLínea dos")]
    [InlineData("Con retorno\r\ny tabulador\t.")]
    public void LineBreaksAndTabsAreAllowed(string text)
    {
        Assert.True(SupportTicketReply.IsValidText(text));
        Assert.Equal(text.Trim(), SupportTicketReply.Start(
            TenantId, SupportTicketId.New(), Guid.CreateVersion7(), SupportReplyChannel.Thread, SupportReplyDelivery.Text, text, null, Actor, Now).Text);
    }

    // AM1: el correo sale sólo como correo, y los canales de Mensajería nunca como correo.
    [Theory]
    [InlineData(SupportReplyChannel.Email, SupportReplyDelivery.Text)]
    [InlineData(SupportReplyChannel.Email, SupportReplyDelivery.Template)]
    [InlineData(SupportReplyChannel.Thread, SupportReplyDelivery.Email)]
    [InlineData(SupportReplyChannel.WhatsAppTemplate, SupportReplyDelivery.Email)]
    public void TheChannelAndTheDeliveryMustAgreeOnEmail(SupportReplyChannel channel, SupportReplyDelivery delivery) =>
        Assert.Throws<ArgumentException>(() => SupportTicketReply.Start(
            TenantId, SupportTicketId.New(), Guid.CreateVersion7(), channel, delivery, "Hola", null, Actor, Now));

    [Theory]
    [InlineData(SupportReplyChannel.Email, SupportReplyDelivery.Email)]
    [InlineData(SupportReplyChannel.Thread, SupportReplyDelivery.Text)]
    [InlineData(SupportReplyChannel.Thread, SupportReplyDelivery.Template)]
    [InlineData(SupportReplyChannel.WhatsAppTemplate, SupportReplyDelivery.Template)]
    public void MatchingChannelsAndDeliveriesStart(SupportReplyChannel channel, SupportReplyDelivery delivery)
    {
        var reply = SupportTicketReply.Start(TenantId, SupportTicketId.New(), Guid.CreateVersion7(), channel, delivery, "Hola", null, Actor, Now);

        Assert.Equal((channel, delivery), (reply.Channel, reply.Delivery));
    }

    // Fix round 1 (revisión del bloque C): la traza dice cómo terminó la respuesta, también cuando falló.
    [Fact]
    public void RecordingAFailedReplyKeepsItsStatusAndFailureCode()
    {
        var ticket = Ticket();
        var reply = Reply(ticket);
        reply.MarkFailed("send_failed", messageId: null, Now.AddMinutes(2));

        ticket.RecordReply(reply, Now.AddMinutes(2));

        var details = JsonDocument.Parse(ticket.Events[^1].Details).RootElement;
        Assert.Equal("Failed", details.GetProperty("status").GetString());
        Assert.Equal("send_failed", details.GetProperty("failureCode").GetString());
        Assert.Equal(JsonValueKind.Null, details.GetProperty("messageId").ValueKind);
    }
}
