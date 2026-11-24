using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.TaskNotices;

/// <summary>
/// Task-notices spec 2026-11-22 §5.4: one pass of the escalation worker. It claims the rows whose time has come (a lease in SQL, so two
/// replicas never take the same row), re-checks each one right before sending, and then sends exactly one WhatsApp or one email.
///
/// <para>The re-check is what makes the escalation honest: the entry may have been read or resolved, the task closed or handed to somebody
/// else, the member suspended. A member who still has QEP open (spec D-1) is not notified at all: the row waits a minute and looks
/// again, and that wait gives back the claimed attempt, so staying connected never uses them up. A failure to ask the presence counts
/// as «not connected»: a notice that goes out twice is a nuisance, one that never goes out defeats the feature.</para>
///
/// <para>Each row is isolated: one that throws is logged, keeps its claim (so it is retried after the lease, up to
/// <see cref="MaxAttempts"/>) and never stops the others. The WhatsApp send is idempotent by <c>ClientId</c> (the escalation id), so a
/// crash between the send and the save cannot send a second message; the email has no such key, which is why its outcome is saved
/// right after it with a token that ignores the host shutdown.</para>
/// </summary>
internal sealed partial class TaskNoticeEscalationProcessor(
    NotificationsDbContext dbContext,
    ITaskNoticeState state,
    ITaskNoticeRecipients recipients,
    ITaskNoticePresence presence,
    ITaskNoticeWhatsApp whatsApp,
    IEmailChannel email,
    IInAppNotificationNames names,
    IOptions<NotificationsOptions> options,
    ILogger<TaskNoticeEscalationProcessor> logger)
{
    internal const int BatchSize = 20;

    internal const int MaxAttempts = 3;

    internal static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    private const string WhatsAppChannel = "whatsapp";
    private const string EmailChannel = "email";
    private const string SentResult = "sent";

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Escalating task notice {EscalationId} failed; it is retried after the lease.")]
    private static partial void LogRowFailed(ILogger logger, Guid escalationId, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Task notice {EscalationId} could not read the member presence; it is treated as not connected.")]
    private static partial void LogPresenceFailed(ILogger logger, Guid escalationId, Exception exception);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Task notice {EscalationId} could not resolve the assigner name; the email goes without it.")]
    private static partial void LogNameFailed(ILogger logger, Guid escalationId, Exception exception);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Task notice {EscalationId} WhatsApp send threw; falling back to email.")]
    private static partial void LogWhatsAppFailed(ILogger logger, Guid escalationId, Exception exception);

    /// <returns>How many rows this pass claimed. Internal for the tests.</returns>
    internal async Task<int> RunAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The lease end doubles as this pass's ownership token: a replica that takes a row over after the lease expired writes its own
        // value, and the token check before sending sees it. Truncated to microseconds, the precision Postgres stores, so it compares.
        var leaseEnd = ToMicroseconds((now + Lease).ToUniversalTime());
        var claimed = await ClaimAsync(now, leaseEnd, cancellationToken);
        foreach (var id in claimed)
        {
            try
            {
                await ProcessAsync(id, now, leaseEnd, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogRowFailed(logger, id, exception);
            }
            finally
            {
                dbContext.ChangeTracker.Clear();
            }
        }

        return claimed.Count;
    }

    // The attempt is counted at claim time, so a row that crashes the worker every time cannot loop forever.
    private async Task<IReadOnlyList<Guid>> ClaimAsync(DateTimeOffset now, DateTimeOffset leaseEnd, CancellationToken cancellationToken)
    {
        var utcNow = now.ToUniversalTime();
        return await dbContext.Database
            .SqlQuery<Guid>(
                $"""
                UPDATE notifications.task_notice_escalations SET claimed_until = {leaseEnd}, attempts = attempts + 1
                WHERE id IN (SELECT id FROM notifications.task_notice_escalations
                             WHERE status = 'Pending' AND due_at <= {utcNow} AND (claimed_until IS NULL OR claimed_until < {utcNow})
                             ORDER BY due_at LIMIT {BatchSize} FOR UPDATE SKIP LOCKED)
                RETURNING id AS "Value"
                """)
            .ToListAsync(cancellationToken);
    }

    private async Task ProcessAsync(Guid id, DateTimeOffset now, DateTimeOffset leaseEnd, CancellationToken cancellationToken)
    {
        var escalation = await dbContext.TaskNoticeEscalations.SingleAsync(row => row.Id == id, cancellationToken);
        // A batch is processed one row at a time; by the time this row's turn comes its lease may have expired and another replica may
        // have taken it (or finished it). Whatever this pass would write then would overwrite that replica's work.
        if (escalation.Status != TaskNoticeEscalationStatus.Pending || escalation.ClaimedUntil != leaseEnd)
        {
            return;
        }

        if (escalation.Attempts > MaxAttempts)
        {
            escalation.Fail(TaskNoticeSkipReasons.AttemptsExhausted, now);
            await dbContext.SaveChangesAsync(CancellationToken.None);
            return;
        }

        var entry = await dbContext.InAppNotifications
            .AsNoTracking()
            .Where(row => row.Id == escalation.InAppNotificationId)
            .Select(row => new { row.ReadAt, row.ResolvedAt })
            .SingleOrDefaultAsync(cancellationToken);
        var ticket = await state.FindAsync(escalation.TenantId, escalation.TicketId, cancellationToken);

        // An entry the retention already deleted takes its row with it (cascade), so a missing one here is a race: nothing left to say.
        // Cheap checks first: a row that is skipped, or deferred because the member is connected (the common case for a connected member,
        // every minute), never resolves the recipient, which is a call into another module.
        var skip = TaskNoticePolicy.SkipReason(
            entryRead: entry?.ReadAt is not null,
            entryResolved: entry is null || entry.ResolvedAt is not null,
            ticket,
            escalation.MemberId);
        if (skip is not null)
        {
            escalation.Skip(skip, now);
            await dbContext.SaveChangesAsync(CancellationToken.None);
            return;
        }

        if (await IsConnectedAsync(escalation, cancellationToken))
        {
            escalation.Defer(now + TaskNoticePolicy.PresenceRecheck);
            await dbContext.SaveChangesAsync(CancellationToken.None);
            return;
        }

        // About to send: only now is the recipient resolved, and an inactive or missing member is skipped here.
        var recipient = await recipients.FindAsync(escalation.TenantId, escalation.MemberId, cancellationToken);
        var memberSkip = TaskNoticePolicy.MemberSkipReason(recipient);
        if (memberSkip is not null)
        {
            escalation.Skip(memberSkip, now);
            await dbContext.SaveChangesAsync(CancellationToken.None);
            return;
        }

        // The checks above read other modules and can take a while: confirm the row is still this pass's, and push the lease forward in
        // the same statement, right before sending. The email has no idempotency key, so this is what keeps a slow pass from mailing a
        // member twice when another replica took the row over meanwhile.
        if (!await RenewOwnershipAsync(escalation.Id, leaseEnd, cancellationToken))
        {
            return;
        }

        await SendAsync(escalation, ticket!, recipient!, now, cancellationToken);
        await dbContext.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<bool> RenewOwnershipAsync(Guid id, DateTimeOffset leaseEnd, CancellationToken cancellationToken)
    {
        var renewed = leaseEnd + Lease;
        var affected = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE notifications.task_notice_escalations SET claimed_until = {renewed}
            WHERE id = {id} AND status = 'Pending' AND claimed_until = {leaseEnd}
            """,
            cancellationToken);
        return affected == 1;
    }

    private static DateTimeOffset ToMicroseconds(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % 10), TimeSpan.Zero);

    private async Task<bool> IsConnectedAsync(TaskNoticeEscalation escalation, CancellationToken cancellationToken)
    {
        try
        {
            return await presence.IsConnectedAsync(escalation.TenantId, escalation.MemberId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogPresenceFailed(logger, escalation.Id, exception);
            return false;
        }
    }

    // One channel, never two: WhatsApp when the tenant chose a connection and the member has a trusted phone; a failed WhatsApp falls
    // through to email (with the code stored), a successful one ends the row.
    private async Task SendAsync(
        TaskNoticeEscalation escalation, TaskNoticeTicket ticket, TaskNoticeRecipient recipient, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (TaskNoticePolicy.Route(ticket.WhatsAppConnectionId, recipient.PhoneE164) is TaskNoticeRoute.WhatsApp route)
        {
            TaskNoticeWhatsAppResult result;
            try
            {
                result = await whatsApp.SendAsync(
                    new TaskNoticeWhatsAppSend(
                        escalation.TenantId,
                        route.ConnectionId,
                        escalation.AssignedByMemberId,
                        ClientId: escalation.Id,
                        route.PhoneE164,
                        recipient.DisplayName,
                        ticket.TicketNumber),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // A WhatsApp path that throws (Meta down, a database error inside Messaging) is a failed channel like any other: the
                // member still gets the email. Letting it escape would only retry WhatsApp until the attempts run out.
                LogWhatsAppFailed(logger, escalation.Id, exception);
                result = new TaskNoticeWhatsAppResult.Failed("send_failed:exception");
            }

            switch (result)
            {
                case TaskNoticeWhatsAppResult.Sent:
                    escalation.RecordAttempt(WhatsAppChannel, SentResult, now);
                    escalation.CompleteBy(TaskNoticeEscalationStatus.Whatsapp, now);
                    return;
                case TaskNoticeWhatsAppResult.Failed failed:
                    escalation.RecordAttempt(WhatsAppChannel, failed.Code, now);
                    break;
            }
        }

        await SendEmailAsync(escalation, ticket, recipient, now, cancellationToken);
    }

    private async Task SendEmailAsync(
        TaskNoticeEscalation escalation, TaskNoticeTicket ticket, TaskNoticeRecipient recipient, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(recipient.Email))
        {
            escalation.RecordAttempt(EmailChannel, "no_email", now);
            escalation.CompleteBy(TaskNoticeEscalationStatus.Failed, now);
            return;
        }

        var message = SupportTaskAssignedEmailTemplate.Render(
            recipient.Email,
            ticket.IsInternal,
            await ActorNameAsync(escalation, cancellationToken),
            ticket.TicketNumber,
            ticket.Subject,
            AccountLinks.SupportTask(options.Value.AppBaseUrl, escalation.TicketId));
        try
        {
            await email.SendAsync(message, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogRowFailed(logger, escalation.Id, exception);
            escalation.RecordAttempt(EmailChannel, "failed", now);
            escalation.CompleteBy(TaskNoticeEscalationStatus.Failed, now);
            return;
        }

        escalation.RecordAttempt(EmailChannel, SentResult, now);
        escalation.CompleteBy(TaskNoticeEscalationStatus.Email, now);
    }

    // The name only adds warmth to the email; never worth losing the notice over.
    private async Task<string?> ActorNameAsync(TaskNoticeEscalation escalation, CancellationToken cancellationToken)
    {
        try
        {
            var found = await names.MemberNamesAsync(escalation.TenantId, [escalation.AssignedByMemberId], cancellationToken);
            return found.GetValueOrDefault(escalation.AssignedByMemberId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogNameFailed(logger, escalation.Id, exception);
            return null;
        }
    }
}
