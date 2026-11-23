using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

/// <summary>
/// Task-notices spec 2026-11-22 D-7: a customer ticket created already assigned to another member tells that member, exactly like assigning
/// it afterwards does. Creating it for oneself or without an assignee tells nobody.
/// </summary>
public sealed class SupportAssignmentNoticesTests
{
    private static readonly Guid TenantId = Guid.Parse("0193a000-0000-7000-8000-000000000001");
    private static readonly Guid Actor = Guid.Parse("0193a000-0000-7000-8000-0000000000a1");
    private static readonly Guid Other = Guid.Parse("0193a000-0000-7000-8000-0000000000a2");
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);

    private static readonly SupportClockContext Clock =
        new(FakeCalendar.WithHolidays, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    private static SupportTicket NewTicket(Guid? assignee)
    {
        var type = SupportTicketTypeDefaults.For(TenantId, Now)[7];
        return SupportTicket.CreateManual(
            SupportTicketId.New(), TenantId, 127, type, "No me llegó la factura", "Pedí la factura del pedido 45 y no llegó.",
            SupportTicketPriority.Medium, ManualIntakeMedium.Phone, SupportReplyChannel.Email, customerId: null,
            new TicketRequester("Ana María", "Gómez", null, "CC", "1020304050", "ana@example.com", "+573001234567", null, "CO", null),
            assignee, Actor, Now, Clock);
    }

    [Fact]
    public void ATicketCreatedForAnotherMemberPublishesAssignedByTheActor()
    {
        var ticket = NewTicket(Other);
        var events = new RecordingEvents();

        SupportAssignmentNotices.PublishOnCreate(events, ticket, Actor, Now);

        var published = Assert.Single(events.Assigned);
        Assert.Same(ticket, published.Ticket);
        Assert.Equal(Actor, published.AssignedBy);
        Assert.Equal(Now, published.OccurredAt);
    }

    [Fact]
    public void ATicketCreatedForTheActorPublishesNothing()
    {
        var events = new RecordingEvents();

        SupportAssignmentNotices.PublishOnCreate(events, NewTicket(Actor), Actor, Now);

        Assert.Empty(events.Assigned);
    }

    [Fact]
    public void AnUnassignedTicketPublishesNothing()
    {
        var events = new RecordingEvents();

        SupportAssignmentNotices.PublishOnCreate(events, NewTicket(null), Actor, Now);

        Assert.Empty(events.Assigned);
    }

    private sealed class RecordingEvents : ISupportIntegrationEvents
    {
        public List<(SupportTicket Ticket, Guid AssignedBy, DateTimeOffset OccurredAt)> Assigned { get; } = [];

        public void PublishTicketAssigned(SupportTicket ticket, Guid assignedByMemberId, DateTimeOffset occurredAt) =>
            Assigned.Add((ticket, assignedByMemberId, occurredAt));

        public void PublishCustomerReplied(SupportTicket ticket, DateTimeOffset occurredAt) => throw new NotSupportedException();

        public void PublishUnassignedAlert(
            SupportTicket ticket, IReadOnlyList<Guid> recipientMemberIds, int thresholdHours, DateTimeOffset unassignedSince, DateTimeOffset occurredAt) =>
            throw new NotSupportedException();

        public void PublishTaskAssigned(SupportTicket ticket, Guid assignedByMemberId, DateTimeOffset occurredAt) =>
            throw new NotSupportedException();

        public void PublishAssigneeReleased(SupportTicket ticket, Guid formerAssigneeMemberId, string reason, DateTimeOffset occurredAt) =>
            throw new NotSupportedException();
    }
}
