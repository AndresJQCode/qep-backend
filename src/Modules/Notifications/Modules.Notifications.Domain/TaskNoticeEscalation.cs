using System.Text.Json;
using System.Text.Json.Nodes;

namespace Modules.Notifications.Domain;

/// <summary>Spec 2026-11-22 §5.4: where an escalation row ends. Stored by name in a <c>varchar(16)</c>.</summary>
public enum TaskNoticeEscalationStatus
{
    Pending,
    Skipped,
    Whatsapp,
    Email,
    Failed,
}

/// <summary>Why the worker did not escalate a row; stored in <c>skip_reason</c> and part of the support vocabulary.</summary>
public static class TaskNoticeSkipReasons
{
    public const string Read = "read";
    public const string Resolved = "resolved";
    public const string NotAssignee = "not_assignee";
    public const string TaskClosed = "task_closed";
    public const string TicketMissing = "ticket_missing";
    public const string MemberInactive = "member_inactive";
    public const string AttemptsExhausted = "attempts_exhausted";
}

/// <summary>
/// Task-notices spec 2026-11-22 §5.4: the pending escalation of one bell entry. If the member has not read it by <see cref="DueAt"/>
/// the worker sends one WhatsApp or one email, never both. Rows are created by raw SQL (<c>ON CONFLICT DO NOTHING</c>) in the same
/// transaction as the entry, so <see cref="Create"/> is for tests and for the worker's own bookkeeping. Every transition is only valid
/// from <see cref="TaskNoticeEscalationStatus.Pending"/>.
/// </summary>
public sealed class TaskNoticeEscalation
{
    private TaskNoticeEscalation()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid InAppNotificationId { get; private set; }

    public Guid TicketId { get; private set; }

    public Guid MemberId { get; private set; }

    public Guid AssignedByMemberId { get; private set; }

    public DateTimeOffset DueAt { get; private set; }

    public TaskNoticeEscalationStatus Status { get; private set; }

    public string? SkipReason { get; private set; }

    public string ChannelAttemptsJson { get; private set; } = "[]";

    public int Attempts { get; private set; }

    public DateTimeOffset? ClaimedUntil { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public static TaskNoticeEscalation Create(
        Guid tenantId,
        Guid inAppNotificationId,
        Guid ticketId,
        Guid memberId,
        Guid assignedByMemberId,
        DateTimeOffset dueAt,
        DateTimeOffset createdAt) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            InAppNotificationId = inAppNotificationId,
            TicketId = ticketId,
            MemberId = memberId,
            AssignedByMemberId = assignedByMemberId,
            DueAt = dueAt,
            Status = TaskNoticeEscalationStatus.Pending,
            CreatedAt = createdAt,
        };

    /// <summary>A replica takes the row for a lease; the attempt is counted at claim time, so a crash cannot loop forever.</summary>
    public void Claim(DateTimeOffset until)
    {
        EnsurePending();
        Attempts++;
        ClaimedUntil = until;
    }

    public void Skip(string reason, DateTimeOffset now)
    {
        EnsurePending();
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Status = TaskNoticeEscalationStatus.Skipped;
        SkipReason = reason;
        ClaimedUntil = null;
        CompletedAt = now;
    }

    /// <summary>The row cannot be worked any more (for example, it was claimed too many times without finishing); <paramref name="reason"/>
    /// goes to <c>skip_reason</c> so the cause is still readable next to the <c>Failed</c> status.</summary>
    public void Fail(string reason, DateTimeOffset now)
    {
        EnsurePending();
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Status = TaskNoticeEscalationStatus.Failed;
        SkipReason = reason;
        ClaimedUntil = null;
        CompletedAt = now;
    }

    public void RecordAttempt(string channel, string result, DateTimeOffset at)
    {
        EnsurePending();
        var log = JsonNode.Parse(ChannelAttemptsJson) as JsonArray ?? [];
        log.Add(new JsonObject { ["channel"] = channel, ["result"] = result, ["at"] = at.ToUniversalTime().ToString("O") });
        ChannelAttemptsJson = log.ToJsonString(JsonSerializerOptions.Default);
    }

    public void CompleteBy(TaskNoticeEscalationStatus status, DateTimeOffset now)
    {
        EnsurePending();
        if (status == TaskNoticeEscalationStatus.Pending)
        {
            throw new ArgumentException("An escalation is completed by an outcome, not by Pending.", nameof(status));
        }

        Status = status;
        ClaimedUntil = null;
        CompletedAt = now;
    }

    /// <summary>
    /// Spec D-1: the member still has QEP open, so the escalation waits a minute more. It gives back the attempt the claim counted
    /// (a deferral is not a failed attempt: staying connected must never exhaust them) and leaves the channel log untouched.
    /// </summary>
    public void Defer(DateTimeOffset nextDueAt)
    {
        EnsurePending();
        DueAt = nextDueAt;
        ClaimedUntil = null;
        Attempts = Math.Max(0, Attempts - 1);
    }

    private void EnsurePending()
    {
        if (Status != TaskNoticeEscalationStatus.Pending)
        {
            throw new InvalidOperationException($"The escalation is {Status}; only a pending one changes.");
        }
    }
}
