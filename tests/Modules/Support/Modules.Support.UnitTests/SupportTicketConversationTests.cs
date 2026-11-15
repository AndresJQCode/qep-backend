using System.Text.Json;
using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

// Fase 2 (spec 2026-11-14 §8.1 y §8.2): el ticket que nace de una conversación, la ventana de seguimiento del hilo (plan E4) y el
// vínculo de una conversación a un ticket que no tenía. FakeCalendar con festivos y zona America/Bogota, como SupportTicketTests.
public sealed class SupportTicketConversationTests
{
    private static readonly Guid TenantId = Guid.Parse("0193a000-0000-7000-8000-000000000001");
    private static readonly Guid Actor = Guid.Parse("0193a000-0000-7000-8000-0000000000a1");
    private static readonly Guid Customer = Guid.Parse("0193a000-0000-7000-8000-0000000000c1");
    private static readonly Guid ConversationId = Guid.Parse("0193a000-0000-7000-8000-0000000000d1");
    private static readonly DateTimeOffset Received = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    private static readonly SupportClockContext Clock =
        new(FakeCalendar.WithHolidays, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    private static readonly SupportConversationRef WhatsApp = new(ConversationId, "WhatsApp");

    private static readonly TicketRequester Person = new(
        "Ana María", "Gómez", null, null, null, null, "+573001234567", null, "CO", null);

    private static SupportTicketType Petition => SupportTicketTypeDefaults.For(TenantId, Received)[0];

    private static SupportTicket FromConversation(bool withoutCustomer = false, TicketRequester? requester = null) =>
        SupportTicket.CreateFromConversation(
            SupportTicketId.New(), TenantId, 1, Petition, "No me llegó la factura", "Lo escribió por WhatsApp.", WhatsApp,
            withoutCustomer ? null : Customer, requester ?? Person, assigneeMemberId: null, Actor, Received, Clock);

    private static SupportTicket Manual() =>
        SupportTicket.CreateManual(
            SupportTicketId.New(), TenantId, 2, Petition, "Asunto", "Descripción", ManualIntakeMedium.Phone,
            SupportReplyChannel.Email, null, Person, null, Actor, Received, Clock);

    private static DateTimeOffset Later(int minutes) => Received.AddMinutes(minutes);

    private static JsonElement DetailsOf(SupportTicketEvent item) => JsonDocument.Parse(item.Details).RootElement;

    [Fact]
    public void ATicketFromAConversationFollowsItsThreadFromCreation()
    {
        var ticket = FromConversation();

        Assert.Equal(SupportTicketIntake.Conversation, ticket.Intake);
        Assert.Equal(SupportReplyChannel.Thread, ticket.ReplyChannel);
        Assert.Null(ticket.ManualMedium);
        Assert.Equal(ConversationId, ticket.ConversationId);
        Assert.Equal("WhatsApp", ticket.ConversationChannel);
        Assert.True(ticket.FollowThread);
        Assert.Equal(Received, ticket.FollowSince);
        Assert.Null(ticket.FollowOffAt);
        Assert.Equal(SupportTicketStatus.New, ticket.Status);
        Assert.Equal(1L, ticket.Version);
        Assert.Equal([SupportTicketEventKind.Created], ticket.Events.Select(item => item.Kind));
    }

    [Fact]
    public void ATicketFromAConversationWithoutCustomerNeedsARequesterName()
    {
        var nameless = Person with { FirstNames = null, LastNames = null };

        var error = Assert.Throws<SupportDomainException>(() => FromConversation(withoutCustomer: true, requester: nameless));

        Assert.Equal(SupportErrorCodes.RequesterRequired, error.Code);
    }

    [Fact]
    public void TurningFollowOffStampsTheTimeAndAnEvent()
    {
        var ticket = FromConversation();

        ticket.SetFollowThread(false, Actor, Later(30));

        Assert.False(ticket.FollowThread);
        Assert.Equal(Later(30), ticket.FollowOffAt);
        Assert.Equal(Received, ticket.FollowSince);
        Assert.Equal(2L, ticket.Version);
        var changed = ticket.Events[^1];
        Assert.Equal(SupportTicketEventKind.FollowThreadChanged, changed.Kind);
        Assert.Equal(Actor, changed.ActorMemberId);
        Assert.False(DetailsOf(changed).GetProperty("follow").GetBoolean());
        Assert.Equal(Later(30), DetailsOf(changed).GetProperty("at").GetDateTimeOffset());
    }

    [Fact]
    public void TurningFollowOnAgainStartsANewWindow()
    {
        var ticket = FromConversation();
        ticket.SetFollowThread(false, Actor, Later(30));

        ticket.SetFollowThread(true, Actor, Later(90));

        Assert.True(ticket.FollowThread);
        Assert.Equal(Later(90), ticket.FollowSince);
        Assert.Null(ticket.FollowOffAt);
        Assert.Equal(3L, ticket.Version);
        Assert.True(DetailsOf(ticket.Events[^1]).GetProperty("follow").GetBoolean());
    }

    [Fact]
    public void SettingTheSameFollowIsANoOp()
    {
        var ticket = FromConversation();

        ticket.SetFollowThread(true, Actor, Later(30));

        Assert.Equal(Received, ticket.FollowSince);
        Assert.Equal(1L, ticket.Version);
        Assert.Single(ticket.Events);
    }

    [Fact]
    public void ClosingStopsFollowing()
    {
        var ticket = FromConversation();
        ticket.ChangeStatus(SupportTicketStatus.Resolved, Actor, Later(10), Clock);

        ticket.ChangeStatus(SupportTicketStatus.Closed, Actor, Later(20), Clock);

        Assert.False(ticket.FollowThread);
        Assert.Equal(ticket.ClosedAt, ticket.FollowOffAt);
        Assert.Equal(Later(20), ticket.FollowOffAt);
    }

    // Revisión del bloque A (AM3): si el asesor ya había apagado el seguimiento, cerrar conserva esa hora. Moverla al cierre metería en
    // la ventana los entrantes que llegaron con el interruptor apagado (plan E4).
    [Fact]
    public void ClosingATicketThatAlreadyStoppedFollowingKeepsTheEarlierTime()
    {
        var ticket = FromConversation();
        ticket.SetFollowThread(false, Actor, Later(5));
        ticket.ChangeStatus(SupportTicketStatus.Resolved, Actor, Later(10), Clock);

        ticket.ChangeStatus(SupportTicketStatus.Closed, Actor, Later(20), Clock);

        Assert.False(ticket.FollowThread);
        Assert.Equal(Later(5), ticket.FollowOffAt);
        Assert.Equal(Later(20), ticket.ClosedAt);
        Assert.False(ticket.CoversInbound(Later(7)));
    }

    [Fact]
    public void CoversInboundOnlyInsideTheWindow()
    {
        var ticket = FromConversation();
        ticket.SetFollowThread(false, Actor, Later(60));

        Assert.False(ticket.CoversInbound(Later(-1)));
        Assert.True(ticket.CoversInbound(Received));
        Assert.True(ticket.CoversInbound(Later(59)));
        Assert.False(ticket.CoversInbound(Later(60)));
        Assert.False(ticket.CoversInbound(Later(61)));

        var closed = FromConversation();
        closed.ChangeStatus(SupportTicketStatus.Resolved, Actor, Later(100), Clock);
        closed.ChangeStatus(SupportTicketStatus.Closed, Actor, Later(200), Clock);

        Assert.False(closed.CoversInbound(Later(10)));
        Assert.False(Manual().CoversInbound(Later(10)));
    }

    [Fact]
    public void ASystemStatusChangeHasNoActor()
    {
        var ticket = FromConversation();
        ticket.ChangeStatus(SupportTicketStatus.WaitingOnCustomer, Actor, Later(10), Clock);

        ticket.ChangeStatus(SupportTicketStatus.Open, null, Later(20), Clock);

        var changed = ticket.Events[^1];
        Assert.Equal(SupportTicketEventKind.StatusChanged, changed.Kind);
        Assert.Null(changed.ActorMemberId);
        Assert.Equal(SupportTicketStatus.Open, ticket.Status);
    }

    [Fact]
    public void LinkingAConversationToATicketThatHasOneThrows()
    {
        var ticket = FromConversation();

        Assert.Throws<InvalidOperationException>(() =>
            ticket.LinkConversation(new SupportConversationRef(Guid.CreateVersion7(), "Instagram"), Actor, Later(5)));
        Assert.Equal(ConversationId, ticket.ConversationId);
    }

    [Fact]
    public void ALinkedConversationWritesConversationLinkedAndStartsFollowing()
    {
        var ticket = Manual();

        ticket.LinkConversation(WhatsApp, Actor, Later(15));

        Assert.Equal(ConversationId, ticket.ConversationId);
        Assert.Equal("WhatsApp", ticket.ConversationChannel);
        Assert.True(ticket.FollowThread);
        Assert.Equal(Later(15), ticket.FollowSince);
        Assert.Null(ticket.FollowOffAt);
        Assert.Equal(2L, ticket.Version);
        var linked = ticket.Events[^1];
        Assert.Equal(SupportTicketEventKind.ConversationLinked, linked.Kind);
        Assert.Equal(ConversationId, DetailsOf(linked).GetProperty("conversationId").GetGuid());
        Assert.Equal("WhatsApp", DetailsOf(linked).GetProperty("channel").GetString());
    }

    // Revisión final (M3): una plantilla a un ticket cerrado vincula la conversación, pero un cerrado no sigue el hilo: la ventana nace
    // apagada, como la deja cerrar.
    [Fact]
    public void ALinkedConversationOnAClosedTicketDoesNotFollowTheThread()
    {
        var ticket = Manual();
        ticket.ChangeStatus(SupportTicketStatus.Resolved, Actor, Later(10), Clock);
        ticket.ChangeStatus(SupportTicketStatus.Closed, Actor, Later(11), Clock);

        ticket.LinkConversation(WhatsApp, Actor, Later(15));

        Assert.Equal(ConversationId, ticket.ConversationId);
        Assert.False(ticket.FollowThread);
        Assert.Equal(Later(15), ticket.FollowOffAt);
        Assert.False(ticket.CoversInbound(Later(16)));
        Assert.Equal(SupportTicketEventKind.ConversationLinked, ticket.Events[^1].Kind);
    }
}
