using System.Text.Json;
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

        reply.MarkFailed("send_failed", Now.AddMinutes(2));

        Assert.Equal(SupportReplyStatus.Failed, reply.Status);
        Assert.Equal("send_failed", reply.FailureCode);
        Assert.Throws<InvalidOperationException>(() => reply.MarkSent(Guid.CreateVersion7(), Now.AddMinutes(3)));
        Assert.Throws<InvalidOperationException>(() => reply.MarkFailed("send_failed", Now.AddMinutes(3)));
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

    // Fix round 1 (revisión del bloque C): la traza dice cómo terminó la respuesta, también cuando falló.
    [Fact]
    public void RecordingAFailedReplyKeepsItsStatusAndFailureCode()
    {
        var ticket = Ticket();
        var reply = Reply(ticket);
        reply.MarkFailed("send_failed", Now.AddMinutes(2));

        ticket.RecordReply(reply, Now.AddMinutes(2));

        var details = JsonDocument.Parse(ticket.Events[^1].Details).RootElement;
        Assert.Equal("Failed", details.GetProperty("status").GetString());
        Assert.Equal("send_failed", details.GetProperty("failureCode").GetString());
        Assert.Equal(JsonValueKind.Null, details.GetProperty("messageId").ValueKind);
    }
}
