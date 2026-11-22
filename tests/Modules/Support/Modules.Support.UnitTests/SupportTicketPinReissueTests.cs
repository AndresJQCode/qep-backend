using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

// Spec 2026-11-16 (P-1, P-3, P-4): el PIN nuevo de un ticket anónimo desde la consola. Sólo anónimos, en cualquier estado, con un
// evento PinReissued sin detalles que sube la versión.
public sealed class SupportTicketPinReissueTests
{
    private static readonly Guid TenantId = Guid.Parse("0193a000-0000-7000-8000-000000000001");
    private static readonly Guid Actor = Guid.Parse("0193a000-0000-7000-8000-0000000000a1");
    private static readonly DateTimeOffset Received = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    private static readonly SupportClockContext Clock =
        new(FakeCalendar.WithHolidays, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    private static readonly TicketRequester Person = new(
        "Ana María", "Gómez", null, "CC", "1020304050", "ana@example.com", "+573001234567", null, "CO", null);

    private static SupportTicketType Petition => SupportTicketTypeDefaults.For(TenantId, Received)[0];

    private static SupportTicket Anonymous() =>
        SupportTicket.CreateManual(
            SupportTicketId.New(), TenantId, 1, Petition, "Queja anónima", "Quiero dejar una queja sin dar mis datos.",
            SupportTicketPriority.Medium, ManualIntakeMedium.Phone, SupportReplyChannel.Portal, null, null, assigneeMemberId: null,
            Actor, Received, Clock, anonymous: true);

    private static SupportTicket Named() =>
        SupportTicket.CreateManual(
            SupportTicketId.New(), TenantId, 2, Petition, "No me llegó la factura", "Pedí la factura del pedido 45 y no llegó.",
            SupportTicketPriority.Medium, ManualIntakeMedium.Phone, SupportReplyChannel.Email, null, Person, assigneeMemberId: null,
            Actor, Received, Clock);

    [Fact]
    public void AnAnonymousTicketRecordsThePinReissueAndRaisesItsVersion()
    {
        var ticket = Anonymous();
        var version = ticket.Version;
        var now = Received.AddDays(1);

        ticket.RecordPinReissued(Actor, now);

        Assert.Equal(version + 1, ticket.Version);
        Assert.Equal(now, ticket.UpdatedAt);
        var reissued = ticket.Events[^1];
        Assert.Equal(SupportTicketEventKind.PinReissued, reissued.Kind);
        Assert.Equal(Actor, reissued.ActorMemberId);
        Assert.Equal(now, reissued.OccurredAt);
        Assert.Equal("{}", reissued.Details);
    }

    [Fact]
    public void ANamedTicketCannotHaveItsPinReissued()
    {
        var ticket = Named();
        var version = ticket.Version;
        var events = ticket.Events.Count;

        var exception = Assert.Throws<SupportDomainException>(() => ticket.RecordPinReissued(Actor, Received.AddDays(1)));

        Assert.Equal(SupportErrorCodes.PinNotAnonymous, exception.Code);
        Assert.Equal("support.ticket.pin_not_anonymous", SupportErrorCodes.PinNotAnonymous);
        Assert.Equal(version, ticket.Version);
        Assert.Equal(events, ticket.Events.Count);
    }

    [Fact]
    public void AClosedAnonymousTicketCanStillHaveItsPinReissued()
    {
        var ticket = Anonymous();
        ticket.ChangeStatus(SupportTicketStatus.Resolved, Actor, Received.AddHours(1), Clock);
        ticket.ChangeStatus(SupportTicketStatus.Closed, Actor, Received.AddHours(2), Clock);

        ticket.RecordPinReissued(Actor, Received.AddDays(1));

        Assert.Equal(SupportTicketEventKind.PinReissued, ticket.Events[^1].Kind);
        Assert.Equal(SupportTicketStatus.Closed, ticket.Status);
    }

    [Fact]
    public void PinReissuedStaysRightBeforeTheTaskDueDateKind() =>
        Assert.Equal(
            [SupportTicketEventKind.PinReissued, SupportTicketEventKind.DueDateChanged],
            Enum.GetValues<SupportTicketEventKind>()[^2..]);
}
