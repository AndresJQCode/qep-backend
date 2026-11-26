using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

/// <summary>
/// Spec 2026-11-22 D-15: <c>UnassignedSince</c> measures continuous time without an assignee in a status that counts (New, Open,
/// WaitingInternal). It is set on entering that situation, cleared on leaving it, and a move between two statuses that count leaves it alone.
/// </summary>
public sealed class SupportTicketUnassignedSinceTests
{
    private static readonly Guid TenantId = Guid.Parse("0193a000-0000-7000-8000-000000000001");
    private static readonly Guid Actor = Guid.Parse("0193a000-0000-7000-8000-0000000000a1");
    private static readonly Guid Assignee = Guid.Parse("0193a000-0000-7000-8000-0000000000a2");
    private static readonly DateTimeOffset Received = new(2026, 11, 16, 15, 0, 0, TimeSpan.Zero);

    private static readonly SupportClockContext Clock =
        new(FakeCalendar.WithHolidays, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    private static SupportTicket NewTicket(Guid? assignee = null) =>
        SupportTicket.CreateManual(
            SupportTicketId.New(), TenantId, 127, SupportTicketTypeDefaults.For(TenantId, Received)[7], "No me llegó la factura",
            "Pedí la factura del pedido 45 y no llegó.", SupportTicketPriority.Medium, ManualIntakeMedium.Phone, SupportReplyChannel.Email,
            customerId: null,
            new TicketRequester("Ana María", "Gómez", null, "CC", "1020304050", "ana@example.com", "+573001234567", null, "CO", null),
            assignee, Actor, Received, Clock);

    private static DateTimeOffset After(int hours) => Received.AddHours(hours);

    [Fact]
    public void CreatingWithoutAssigneeSetsUnassignedSince()
    {
        Assert.Equal(Received, NewTicket().UnassignedSince);
        Assert.Null(NewTicket(Assignee).UnassignedSince);
    }

    [Fact]
    public void AssigningClearsItAndUnassigningSetsItAgain()
    {
        var ticket = NewTicket();

        ticket.Assign(Assignee, Actor, After(1));
        Assert.Null(ticket.UnassignedSince);

        ticket.Assign(null, Actor, After(2));
        Assert.Equal(After(2), ticket.UnassignedSince);
    }

    [Fact]
    public void WaitingOnCustomerClearsItAndReopeningSetsItAgain()
    {
        var ticket = NewTicket();
        ticket.ChangeStatus(SupportTicketStatus.WaitingOnCustomer, Actor, After(1), Clock);
        Assert.Null(ticket.UnassignedSince);

        // The thread follower reopening it (actor null) is a move into a status that counts: the clock starts from that moment.
        ticket.ChangeStatus(SupportTicketStatus.Open, null, After(120), Clock);

        Assert.Equal(After(120), ticket.UnassignedSince);
    }

    [Fact]
    public void MovingBetweenCountingStatesKeepsIt()
    {
        var ticket = NewTicket();

        ticket.ChangeStatus(SupportTicketStatus.Open, Actor, After(1), Clock);
        Assert.Equal(Received, ticket.UnassignedSince);

        ticket.ChangeStatus(SupportTicketStatus.WaitingInternal, Actor, After(2), Clock);
        Assert.Equal(Received, ticket.UnassignedSince);

        ticket.ChangeStatus(SupportTicketStatus.Open, Actor, After(3), Clock);
        Assert.Equal(Received, ticket.UnassignedSince);
    }

    [Fact]
    public void ResolvingClearsIt()
    {
        var ticket = NewTicket();

        ticket.ChangeStatus(SupportTicketStatus.Resolved, Actor, After(1), Clock);

        Assert.Null(ticket.UnassignedSince);

        // Resolved → Open without an assignee is a new stretch.
        ticket.ChangeStatus(SupportTicketStatus.Open, Actor, After(2), Clock);
        Assert.Equal(After(2), ticket.UnassignedSince);
    }

    [Fact]
    public void UnassigningWhileWaitingOnTheCustomerDoesNotStartTheClock()
    {
        var ticket = NewTicket(Assignee);
        ticket.ChangeStatus(SupportTicketStatus.WaitingOnCustomer, Actor, After(1), Clock);

        ticket.Assign(null, Actor, After(2));
        Assert.Null(ticket.UnassignedSince);

        ticket.ChangeStatus(SupportTicketStatus.Open, null, After(3), Clock);
        Assert.Equal(After(3), ticket.UnassignedSince);
    }

    [Fact]
    public void AnAssignedTicketComingBackToOpenStaysNull()
    {
        var ticket = NewTicket(Assignee);
        ticket.ChangeStatus(SupportTicketStatus.WaitingOnCustomer, Actor, After(1), Clock);

        ticket.ChangeStatus(SupportTicketStatus.Open, null, After(2), Clock);

        Assert.Null(ticket.UnassignedSince);
    }
}
