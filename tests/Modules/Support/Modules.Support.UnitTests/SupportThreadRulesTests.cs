using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

/// <summary>Spec 2026-11-22 §6: «el cliente respondió» only when a waiting ticket with an assignee is reopened by someone else.</summary>
public sealed class SupportThreadRulesTests
{
    private static readonly Guid TenantId = Guid.Parse("0193a000-0000-7000-8000-000000000001");
    private static readonly Guid Assignee = Guid.Parse("0193a000-0000-7000-8000-0000000000a1");
    private static readonly Guid OtherMember = Guid.Parse("0193a000-0000-7000-8000-0000000000a2");
    private static readonly DateTimeOffset Received = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);

    private static readonly SupportClockContext Clock =
        new(FakeCalendar.WithHolidays, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    private static readonly SupportReadContext Context = new(Clock, Now, new DateOnly(2026, 11, 20), DueSoonBusinessDays: 3);

    private static SupportTicket NewTicket(Guid? assignee, SupportTicketStatus status)
    {
        var type = SupportTicketTypeDefaults.For(TenantId, Received)[7];
        var ticket = SupportTicket.CreateManual(
            SupportTicketId.New(), TenantId, 127, type, "No me llegó la factura", "Pedí la factura del pedido 45 y no llegó.",
            SupportTicketPriority.Medium, ManualIntakeMedium.Phone, SupportReplyChannel.Email, customerId: null,
            new TicketRequester("Ana María", "Gómez", null, "CC", "1020304050", "ana@example.com", "+573001234567", null, "CO", null),
            assignee, Assignee, Received, Clock);
        // New cannot go straight to WaitingInternal (spec §7.1): it passes through Open.
        if (status is SupportTicketStatus.WaitingInternal)
        {
            ticket.ChangeStatus(SupportTicketStatus.Open, Assignee, Received.AddMinutes(1), Clock);
            ticket.ChangeStatus(status, Assignee, Received.AddMinutes(2), Clock);
        }
        else if (status is not SupportTicketStatus.New)
        {
            ticket.ChangeStatus(status, Assignee, Received.AddMinutes(1), Clock);
        }

        return ticket;
    }

    [Fact]
    public void ReopeningAnAssignedTicketPublishesCustomerReplied()
    {
        var ticket = NewTicket(Assignee, SupportTicketStatus.WaitingOnCustomer);
        var events = new RecordingEvents();
        var audit = new RecordingAudit();

        SupportThreadRules.ReopenIfWaiting(ticket, Context, audit, events, actorMemberId: null);

        Assert.Equal(SupportTicketStatus.Open, ticket.Status);
        var published = Assert.Single(events.Replies);
        Assert.Same(ticket, published.Ticket);
        Assert.Equal(Now, published.OccurredAt);
        Assert.Equal(1, audit.SystemEntries);
    }

    [Fact]
    public void ReopeningAnUnassignedTicketPublishesNothing()
    {
        var ticket = NewTicket(null, SupportTicketStatus.WaitingOnCustomer);
        var events = new RecordingEvents();

        SupportThreadRules.ReopenIfWaiting(ticket, Context, new RecordingAudit(), events, actorMemberId: null);

        // It still reopens: only the immediate notice needs someone to tell (the unassigned alert covers the other case).
        Assert.Equal(SupportTicketStatus.Open, ticket.Status);
        Assert.Empty(events.Replies);
    }

    [Theory]
    [InlineData(SupportTicketStatus.New)]
    [InlineData(SupportTicketStatus.Open)]
    [InlineData(SupportTicketStatus.WaitingInternal)]
    public void ATicketThatWasNotWaitingPublishesNothing(SupportTicketStatus status)
    {
        var ticket = NewTicket(Assignee, status);
        var events = new RecordingEvents();
        var audit = new RecordingAudit();

        SupportThreadRules.ReopenIfWaiting(ticket, Context, audit, events, actorMemberId: null);

        Assert.Equal(status, ticket.Status);
        Assert.Empty(events.Replies);
        Assert.Equal(0, audit.SystemEntries);
    }

    [Fact]
    public void TheAssigneeClassifyingTheMessageIsNotNotified()
    {
        var ticket = NewTicket(Assignee, SupportTicketStatus.WaitingOnCustomer);
        var events = new RecordingEvents();

        SupportThreadRules.ReopenIfWaiting(ticket, Context, new RecordingAudit(), events, actorMemberId: Assignee);

        Assert.Equal(SupportTicketStatus.Open, ticket.Status);
        Assert.Empty(events.Replies);
    }

    [Fact]
    public void AnotherMemberClassifyingTheMessageStillNotifiesTheAssignee()
    {
        var ticket = NewTicket(Assignee, SupportTicketStatus.WaitingOnCustomer);
        var events = new RecordingEvents();

        SupportThreadRules.ReopenIfWaiting(ticket, Context, new RecordingAudit(), events, actorMemberId: OtherMember);

        Assert.Single(events.Replies);
    }

    private sealed class RecordingEvents : ISupportIntegrationEvents
    {
        public List<(SupportTicket Ticket, DateTimeOffset OccurredAt)> Replies { get; } = [];

        public void PublishCustomerReplied(SupportTicket ticket, DateTimeOffset occurredAt) => Replies.Add((ticket, occurredAt));

        public void PublishUnassignedAlert(
            SupportTicket ticket, IReadOnlyList<Guid> recipientMemberIds, int thresholdHours, DateTimeOffset unassignedSince, DateTimeOffset occurredAt) =>
            throw new NotSupportedException("Not part of this rule.");

        public void PublishTaskAssigned(SupportTicket ticket, Guid assignedByMemberId, DateTimeOffset occurredAt) =>
            throw new NotSupportedException("Not part of this rule.");

        public void PublishTicketAssigned(SupportTicket ticket, Guid assignedByMemberId, DateTimeOffset occurredAt) =>
            throw new NotSupportedException("Not part of this rule.");

        public void PublishAssigneeReleased(SupportTicket ticket, Guid formerAssigneeMemberId, string reason, DateTimeOffset occurredAt) =>
            throw new NotSupportedException("Not part of this rule.");
    }

    private sealed class RecordingAudit : ISupportAuditPublisher
    {
        public int SystemEntries { get; private set; }

        public void Publish(
            Guid tenantId, Guid actorId, string action, string resourceType, string resourceId, string outcome,
            IReadOnlyCollection<string> changedFields, DateTimeOffset occurredAt) =>
            throw new NotSupportedException("The rule audits as the system.");

        public void PublishSystem(
            Guid tenantId, string action, string resourceType, string resourceId, IReadOnlyCollection<string> changedFields,
            DateTimeOffset occurredAt) => SystemEntries++;
    }
}
