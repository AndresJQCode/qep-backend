using Modules.Notifications.Application;
using Modules.Notifications.Domain;

namespace Modules.Notifications.UnitTests;

/// <summary>Task-notices spec 2026-11-22 §5.4, D-5 and D-7: when an escalation is skipped and which channel it takes.</summary>
public sealed class TaskNoticePolicyTests
{
    private static readonly Guid Member = Guid.CreateVersion7();

    private static TaskNoticeTicket Ticket(bool isInternal = true, bool isOpen = true, Guid? assignee = null) =>
        new(isInternal, isOpen, assignee ?? Member, "SOP-000127", "Cambiar la impresora", null);

    private static TaskNoticeRecipient Recipient(bool active = true) => new(active, "Ana Gomez", "ana@example.com", "+573001234567");

    [Fact]
    public void ResolvedWinsOverRead() =>
        Assert.Equal(TaskNoticeSkipReasons.Resolved, TaskNoticePolicy.SkipReason(true, true, Ticket(), Member, Recipient()));

    [Fact]
    public void AReadEntryIsSkipped() =>
        Assert.Equal(TaskNoticeSkipReasons.Read, TaskNoticePolicy.SkipReason(true, false, Ticket(), Member, Recipient()));

    [Fact]
    public void AMissingTicketIsSkipped() =>
        Assert.Equal(TaskNoticeSkipReasons.TicketMissing, TaskNoticePolicy.SkipReason(false, false, null, Member, Recipient()));

    [Fact]
    public void AClosedTaskIsSkipped() =>
        Assert.Equal(TaskNoticeSkipReasons.TaskClosed, TaskNoticePolicy.SkipReason(false, false, Ticket(isOpen: false), Member, Recipient()));

    [Fact]
    public void AnotherAssigneeIsSkipped() =>
        Assert.Equal(
            TaskNoticeSkipReasons.NotAssignee,
            TaskNoticePolicy.SkipReason(false, false, Ticket(assignee: Guid.CreateVersion7()), Member, Recipient()));

    [Fact]
    public void AnUnassignedTicketIsSkippedAsNotAssignee() =>
        Assert.Equal(
            TaskNoticeSkipReasons.NotAssignee,
            TaskNoticePolicy.SkipReason(false, false, Ticket() with { AssigneeMemberId = null }, Member, Recipient()));

    [Fact]
    public void AMissingRecipientIsSkipped() =>
        Assert.Equal(TaskNoticeSkipReasons.MemberInactive, TaskNoticePolicy.SkipReason(false, false, Ticket(), Member, null));

    [Fact]
    public void AnInactiveRecipientIsSkipped() =>
        Assert.Equal(TaskNoticeSkipReasons.MemberInactive, TaskNoticePolicy.SkipReason(false, false, Ticket(), Member, Recipient(active: false)));

    [Fact]
    public void OpenTaskOfTheSameActiveMemberIsSent() =>
        Assert.Null(TaskNoticePolicy.SkipReason(false, false, Ticket(), Member, Recipient()));

    // The worker asks these before it reads the recipient or the presence: the cheap checks cannot depend on the recipient.
    [Fact]
    public void TheTicketChecksNeverLookAtTheRecipient()
    {
        Assert.Equal(TaskNoticeSkipReasons.Resolved, TaskNoticePolicy.SkipReason(true, true, Ticket(), Member));
        Assert.Equal(TaskNoticeSkipReasons.Read, TaskNoticePolicy.SkipReason(true, false, Ticket(), Member));
        Assert.Equal(TaskNoticeSkipReasons.TicketMissing, TaskNoticePolicy.SkipReason(false, false, null, Member));
        Assert.Equal(TaskNoticeSkipReasons.TaskClosed, TaskNoticePolicy.SkipReason(false, false, Ticket(isOpen: false), Member));
        Assert.Equal(
            TaskNoticeSkipReasons.NotAssignee, TaskNoticePolicy.SkipReason(false, false, Ticket(assignee: Guid.CreateVersion7()), Member));
        Assert.Null(TaskNoticePolicy.SkipReason(false, false, Ticket(), Member));
    }

    [Fact]
    public void OnlyAnActiveRecipientPassesTheMemberCheck()
    {
        Assert.Null(TaskNoticePolicy.MemberSkipReason(Recipient()));
        Assert.Equal(TaskNoticeSkipReasons.MemberInactive, TaskNoticePolicy.MemberSkipReason(Recipient(active: false)));
        Assert.Equal(TaskNoticeSkipReasons.MemberInactive, TaskNoticePolicy.MemberSkipReason(null));
    }

    // Spec D-7: a customer ticket is escalated like a task; only a ticket that does not exist is «missing».
    [Fact]
    public void ACustomerTicketIsNotSkippedAsMissing() =>
        Assert.Null(TaskNoticePolicy.SkipReason(false, false, Ticket(isInternal: false), Member, Recipient()));

    [Fact]
    public void RouteWithoutConnectionIsEmail()
    {
        var route = TaskNoticePolicy.Route(null, "+573001234567");

        Assert.Equal("no_connection", Assert.IsType<TaskNoticeRoute.EmailOnly>(route).Reason);
    }

    [Fact]
    public void RouteWithoutPhoneIsEmail()
    {
        var route = TaskNoticePolicy.Route(Guid.CreateVersion7(), null);

        Assert.Equal("no_phone", Assert.IsType<TaskNoticeRoute.EmailOnly>(route).Reason);
    }

    [Fact]
    public void RouteWithBothIsWhatsApp()
    {
        var connection = Guid.CreateVersion7();

        var route = Assert.IsType<TaskNoticeRoute.WhatsApp>(TaskNoticePolicy.Route(connection, "+573001234567"));

        Assert.Equal((connection, "+573001234567"), (route.ConnectionId, route.PhoneE164));
    }

    [Fact]
    public void ThePresenceRecheckIsOneMinute() =>
        Assert.Equal(TimeSpan.FromMinutes(1), TaskNoticePolicy.PresenceRecheck);
}
