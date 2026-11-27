using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.HrBirthdays;

/// <summary>
/// HR birthday notices, spec 2026-11-26 §5.3: one pass of the delivery worker. Calqued on <c>TaskNoticeEscalationProcessor</c>: it claims the
/// pending rows with a lease in SQL (<c>FOR UPDATE SKIP LOCKED</c>, so two replicas never take the same row), checks each one right before
/// sending and sends exactly the channel the row is for.
///
/// <para>Unlike a task notice there is no presence deferral, no read check and no fallback between channels: the owner wants both channels
/// regardless of the bell, and one failing never blocks the other (they are different rows). The checks, in order: a row whose attempts ran out
/// is <c>Failed attempts_exhausted</c>; one past its <c>expires_at</c> (the end of the run date in the tenant's time zone) is <c>Skipped stale</c>,
/// because a «Hoy» sent the next day would be wrong; the recipient is resolved only then (a call into another module) and an inactive or missing
/// member is <c>Skipped member_inactive</c>; WhatsApp needs a phone and email needs an address, else <c>Skipped no_phone</c> / <c>no_email</c>.</para>
///
/// <para>Each row is isolated: one that throws is logged, keeps its claim (so it is retried after the lease, up to <see cref="MaxAttempts"/>)
/// and never stops the others. The WhatsApp send is idempotent by <c>ClientId</c> (the delivery id), so a crash between the send and the save
/// cannot send a second message; the email has no such key, which is why the ownership is renewed right before it and its outcome is saved right
/// after with a token that ignores the host shutdown.</para>
/// </summary>
internal sealed partial class HrBirthdayDeliveryProcessor(
    NotificationsDbContext dbContext,
    ITaskNoticeRecipients recipients,
    IHrBirthdayWhatsApp whatsApp,
    IEmailChannel email,
    IOptions<NotificationsOptions> options,
    ILogger<HrBirthdayDeliveryProcessor> logger)
{
    internal const int BatchSize = 20;

    internal const int MaxAttempts = 3;

    internal static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Sending birthday delivery {DeliveryId} failed; it is retried after the lease.")]
    private static partial void LogRowFailed(ILogger logger, Guid deliveryId, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Birthday delivery {DeliveryId} WhatsApp send threw; it is recorded as failed.")]
    private static partial void LogWhatsAppFailed(ILogger logger, Guid deliveryId, Exception exception);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Birthday delivery {DeliveryId} email send threw; it is recorded as failed.")]
    private static partial void LogEmailFailed(ILogger logger, Guid deliveryId, Exception exception);

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
                UPDATE notifications.hr_birthday_deliveries SET claimed_until = {leaseEnd}, attempts = attempts + 1
                WHERE id IN (SELECT id FROM notifications.hr_birthday_deliveries
                             WHERE status = 'Pending' AND (claimed_until IS NULL OR claimed_until < {utcNow})
                             ORDER BY created_at LIMIT {BatchSize} FOR UPDATE SKIP LOCKED)
                RETURNING id AS "Value"
                """)
            .ToListAsync(cancellationToken);
    }

    private async Task ProcessAsync(Guid id, DateTimeOffset now, DateTimeOffset leaseEnd, CancellationToken cancellationToken)
    {
        var delivery = await dbContext.HrBirthdayDeliveries.SingleAsync(row => row.Id == id, cancellationToken);
        // A batch is processed one row at a time; by the time this row's turn comes its lease may have expired and another replica may
        // have taken it (or finished it). Whatever this pass would write then would overwrite that replica's work.
        if (delivery.Status != HrBirthdayDeliveryStatus.Pending || delivery.ClaimedUntil != leaseEnd)
        {
            return;
        }

        if (delivery.Attempts > MaxAttempts)
        {
            delivery.Fail(HrBirthdayDeliverySkipReasons.AttemptsExhausted, now);
            await dbContext.SaveChangesAsync(CancellationToken.None);
            return;
        }

        if (now >= delivery.ExpiresAt)
        {
            delivery.Skip(HrBirthdayDeliverySkipReasons.Stale, now);
            await dbContext.SaveChangesAsync(CancellationToken.None);
            return;
        }

        // About to send: only now is the recipient resolved, and an inactive or missing member is skipped here.
        var recipient = await recipients.FindAsync(delivery.TenantId, delivery.MemberId, cancellationToken);
        if (recipient is not { IsActive: true })
        {
            delivery.Skip(HrBirthdayDeliverySkipReasons.MemberInactive, now);
            await dbContext.SaveChangesAsync(CancellationToken.None);
            return;
        }

        var isWhatsApp = delivery.Channel == HrBirthdayDeliveryChannels.WhatsApp;
        if (isWhatsApp && string.IsNullOrWhiteSpace(recipient.PhoneE164))
        {
            delivery.Skip(HrBirthdayDeliverySkipReasons.NoPhone, now);
            await dbContext.SaveChangesAsync(CancellationToken.None);
            return;
        }

        if (!isWhatsApp && string.IsNullOrWhiteSpace(recipient.Email))
        {
            delivery.Skip(HrBirthdayDeliverySkipReasons.NoEmail, now);
            await dbContext.SaveChangesAsync(CancellationToken.None);
            return;
        }

        // The checks above read another module and can take a while: confirm the row is still this pass's, and push the lease forward in the
        // same statement, right before sending. The email has no idempotency key, so this is what keeps a slow pass from mailing a member twice
        // when another replica took the row over meanwhile.
        if (!await RenewOwnershipAsync(delivery.Id, leaseEnd, cancellationToken))
        {
            return;
        }

        var digest = Digest.From(delivery.PayloadJson);
        if (isWhatsApp)
        {
            await SendWhatsAppAsync(delivery, recipient, digest, now, cancellationToken);
        }
        else
        {
            await SendEmailAsync(delivery, recipient, digest, now, cancellationToken);
        }

        await dbContext.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<bool> RenewOwnershipAsync(Guid id, DateTimeOffset leaseEnd, CancellationToken cancellationToken)
    {
        var renewed = leaseEnd + Lease;
        var affected = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE notifications.hr_birthday_deliveries SET claimed_until = {renewed}
            WHERE id = {id} AND status = 'Pending' AND claimed_until = {leaseEnd}
            """,
            cancellationToken);
        return affected == 1;
    }

    private static DateTimeOffset ToMicroseconds(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % 10), TimeSpan.Zero);

    private async Task SendWhatsAppAsync(
        HrBirthdayDelivery delivery, TaskNoticeRecipient recipient, Digest digest, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (delivery.ConnectionId is not { } connectionId)
        {
            // The consumer only writes a WhatsApp row with a connection; a row without one cannot ever be sent.
            delivery.Fail("connection_missing", now);
            return;
        }

        TaskNoticeWhatsAppResult result;
        try
        {
            result = await whatsApp.SendAsync(
                new HrBirthdayWhatsAppSend(
                    delivery.TenantId,
                    connectionId,
                    delivery.SentByMemberId,
                    ClientId: delivery.Id,
                    recipient.PhoneE164!,
                    // Meta rejects an empty variable: a member without a visible name is greeted as the team.
                    string.IsNullOrWhiteSpace(recipient.DisplayName) ? "equipo" : recipient.DisplayName,
                    HrBirthdayWhatsAppText.Names(digest.Today, digest.TodayTotal, HrBirthdayWhatsAppText.MaxNamesLength),
                    HrBirthdayWhatsAppText.Date(digest.UpcomingDate),
                    HrBirthdayWhatsAppText.Names(digest.Upcoming, digest.UpcomingTotal, HrBirthdayWhatsAppText.MaxNamesLength)),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A WhatsApp path that throws (Meta down, a database error inside Messaging) is a failed channel like any other: the email row is
            // another row and goes on.
            LogWhatsAppFailed(logger, delivery.Id, exception);
            result = new TaskNoticeWhatsAppResult.Failed("send_failed:exception");
        }

        switch (result)
        {
            case TaskNoticeWhatsAppResult.Sent:
                delivery.MarkSent(now);
                break;
            case TaskNoticeWhatsAppResult.Failed failed:
                delivery.Fail(failed.Code, now);
                break;
        }
    }

    private async Task SendEmailAsync(
        HrBirthdayDelivery delivery, TaskNoticeRecipient recipient, Digest digest, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var message = HrBirthdayDigestEmailTemplate.Render(
            recipient.Email!,
            [.. digest.TodayPeople.Select(person => (person.EmployeeId, person.EmployeeName))],
            digest.TodayTotal,
            [.. digest.UpcomingPeople.Select(person => (person.EmployeeId, person.EmployeeName))],
            digest.UpcomingTotal,
            digest.UpcomingDate,
            options.Value.AppBaseUrl);
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
            LogEmailFailed(logger, delivery.Id, exception);
            delivery.Fail("email_failed", now);
            return;
        }

        delivery.MarkSent(now);
    }

    private sealed record Person(Guid EmployeeId, string EmployeeName);

    // The part of the digest the row stores, as the consumer wrote it: names, totals and the upcoming date.
    private sealed record Digest(
        IReadOnlyList<Person> TodayPeople, int TodayTotal, IReadOnlyList<Person> UpcomingPeople, int UpcomingTotal, DateOnly? UpcomingDate)
    {
        public IReadOnlyList<string> Today => [.. TodayPeople.Select(person => person.EmployeeName)];

        public IReadOnlyList<string> Upcoming => [.. UpcomingPeople.Select(person => person.EmployeeName)];

        public static Digest From(string payloadJson)
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            return new Digest(
                PeopleOf(root, "today"),
                root.GetProperty("todayTotal").GetInt32(),
                PeopleOf(root, "upcoming"),
                root.GetProperty("upcomingTotal").GetInt32(),
                root.TryGetProperty("upcomingDate", out var date) && date.ValueKind == JsonValueKind.String
                    ? DateOnly.ParseExact(date.GetString()!, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
                    : null);
        }

        private static List<Person> PeopleOf(JsonElement root, string name) =>
            [.. root.GetProperty(name).EnumerateArray().Select(person => new Person(
                person.GetProperty("employeeId").GetGuid(), person.GetProperty("employeeName").GetString() ?? string.Empty))];
    }
}
