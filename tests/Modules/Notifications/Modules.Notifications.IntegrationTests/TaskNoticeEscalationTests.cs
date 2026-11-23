using System.Collections.Concurrent;
using System.Text.Json;
using BuildingBlocks.Application.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Notifications.Application;
using Modules.Notifications.Infrastructure.TaskNotices;
using Npgsql;
using Testcontainers.PostgreSql;
using static Modules.Notifications.IntegrationTests.InAppTestSupport;
using static Modules.Notifications.IntegrationTests.NotificationsDeliveryHarness;

namespace Modules.Notifications.IntegrationTests;

/// <summary>
/// Task-notices spec 2026-11-22 §5.4 and D-1: the escalation worker's processor against a real database, with doubles for the four ports
/// (ticket, recipient, presence, WhatsApp) and the email channel. <c>RunAsync</c> takes an explicit <c>now</c>, so the lease, the presence
/// deferral and the attempt count are exercised without waiting.
/// </summary>
public sealed class TaskNoticeEscalationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 15, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- timing and presence ----

    [Fact]
    public async Task NothingHappensBeforeTheDueTime()
    {
        await using var rig = await Rig.StartAsync();
        var row = await rig.SeedAsync(dueAt: T0.AddMinutes(5));

        var processed = await rig.RunAsync(T0);

        Assert.Equal(0, processed);
        Assert.Equal("Pending", (await rig.ReadAsync(row.EscalationId)).Status);
        Assert.Empty(rig.WhatsApp.Sends);
        Assert.Empty(rig.Email.Sent);
    }

    [Fact]
    public async Task AConnectedMemberIsDeferredAMinuteWithoutSending()
    {
        await using var rig = await Rig.StartAsync();
        rig.Presence.Connected = true;
        var row = await rig.SeedAsync();

        await rig.RunAsync(T0);

        var state = await rig.ReadAsync(row.EscalationId);
        Assert.Equal("Pending", state.Status);
        Assert.Equal(T0.AddMinutes(1), state.DueAt);
        Assert.Equal(0, state.Attempts);
        Assert.Null(state.ClaimedUntil);
        Assert.Equal("[]", state.ChannelAttempts.GetRawText());
        Assert.Empty(rig.WhatsApp.Sends);
        Assert.Empty(rig.Email.Sent);
    }

    // The cheap checks and the presence come before the recipient: a connected member is asked about every minute and must cost nothing.
    [Fact]
    public async Task AConnectedMemberIsDeferredWithoutResolvingTheRecipient()
    {
        await using var rig = await Rig.StartAsync();
        rig.Presence.Connected = true;
        await rig.SeedAsync();

        await rig.RunAsync(T0);

        Assert.Equal(0, rig.Recipients.Calls);
    }

    [Fact]
    public async Task ASkippedRowIsSkippedWithoutResolvingTheRecipient()
    {
        await using var rig = await Rig.StartAsync();
        var row = await rig.SeedAsync(readAt: T0.AddMinutes(-5));

        await rig.RunAsync(T0);

        Assert.Equal("read", (await rig.ReadAsync(row.EscalationId)).SkipReason);
        Assert.Equal(0, rig.Recipients.Calls);
    }

    [Fact]
    public async Task AnInactiveMemberIsSkippedOnceDisconnected()
    {
        await using var rig = await Rig.StartAsync();
        var row = await rig.SeedAsync();
        rig.Recipients.Members[row.MemberId] = rig.Recipient with { IsActive = false };

        await rig.RunAsync(T0);

        var state = await rig.ReadAsync(row.EscalationId);
        Assert.Equal("Skipped", state.Status);
        Assert.Equal("member_inactive", state.SkipReason);
        Assert.Empty(rig.WhatsApp.Sends);
        Assert.Empty(rig.Email.Sent);
    }

    // 25 due rows of connected members: the first pass claims 20 and defers them a minute; without a second pass in the same tick the
    // other 5 would wait for the next minute, and so on for as long as the first ones keep being deferred.
    [Fact]
    public async Task AFullBatchIsFollowedByAnotherPassInTheSameTick()
    {
        await using var rig = await Rig.StartAsync();
        rig.Presence.Connected = true;
        var rows = new List<SeededRow>();
        for (var index = 0; index < TaskNoticeEscalationProcessor.BatchSize + 5; index++)
        {
            rows.Add(await rig.SeedAsync(dueAt: T0.AddMinutes(-30).AddSeconds(index)));
        }

        await rig.TickAsync();

        foreach (var row in rows)
        {
            var state = await rig.ReadAsync(row.EscalationId);
            Assert.True(state.DueAt > T0, $"Row {row.EscalationId} was never looked at in the tick.");
        }
    }

    [Fact]
    public async Task StayingConnectedNeverExhaustsTheAttempts()
    {
        await using var rig = await Rig.StartAsync();
        rig.Presence.Connected = true;
        var row = await rig.SeedAsync();

        for (var minute = 0; minute < 5; minute++)
        {
            await rig.RunAsync(T0.AddMinutes(minute));
        }

        var state = await rig.ReadAsync(row.EscalationId);
        Assert.Equal("Pending", state.Status);
        Assert.Null(state.SkipReason);
        Assert.Equal(0, state.Attempts);
    }

    [Fact]
    public async Task EscalatesOnceTheMemberDisconnects()
    {
        await using var rig = await Rig.StartAsync();
        rig.Presence.Connected = true;
        var row = await rig.SeedAsync();
        await rig.RunAsync(T0);

        rig.Presence.Connected = false;
        await rig.RunAsync(T0.AddMinutes(1));

        Assert.Equal("Whatsapp", (await rig.ReadAsync(row.EscalationId)).Status);
        Assert.Single(rig.WhatsApp.Sends);
    }

    [Fact]
    public async Task AConnectedMemberWhoReadsIsSkippedAsRead()
    {
        await using var rig = await Rig.StartAsync();
        rig.Presence.Connected = true;
        var row = await rig.SeedAsync();
        await rig.RunAsync(T0);

        await rig.ExecuteAsync($"UPDATE notifications.in_app_notifications SET read_at = '{T0.AddSeconds(30):O}' WHERE id = '{row.EntryId}'");
        await rig.RunAsync(T0.AddMinutes(1));

        var state = await rig.ReadAsync(row.EscalationId);
        Assert.Equal("Skipped", state.Status);
        Assert.Equal("read", state.SkipReason);
        Assert.Empty(rig.WhatsApp.Sends);
        Assert.Empty(rig.Email.Sent);
    }

    [Fact]
    public async Task APresenceFailureEscalates()
    {
        await using var rig = await Rig.StartAsync();
        rig.Presence.Throws = true;
        var row = await rig.SeedAsync();

        await rig.RunAsync(T0);

        Assert.Equal("Whatsapp", (await rig.ReadAsync(row.EscalationId)).Status);
        Assert.Single(rig.WhatsApp.Sends);
    }

    // ---- the re-check at send time ----

    [Fact]
    public async Task DoesNotEscalateAReadEntry()
    {
        await using var rig = await Rig.StartAsync();
        var row = await rig.SeedAsync(readAt: T0.AddMinutes(-5));

        await rig.RunAsync(T0);

        await AssertSkippedAsync(rig, row, "read");
    }

    [Fact]
    public async Task DoesNotEscalateAResolvedEntry()
    {
        await using var rig = await Rig.StartAsync();
        var row = await rig.SeedAsync(resolvedAt: T0.AddMinutes(-5));

        await rig.RunAsync(T0);

        await AssertSkippedAsync(rig, row, "resolved");
    }

    [Fact]
    public async Task DoesNotEscalateAReassignedTask()
    {
        await using var rig = await Rig.StartAsync();
        var row = await rig.SeedAsync();
        rig.State.Tickets[row.TicketId] = rig.OpenTicket(row) with { AssigneeMemberId = Guid.CreateVersion7() };

        await rig.RunAsync(T0);

        await AssertSkippedAsync(rig, row, "not_assignee");
    }

    [Fact]
    public async Task DoesNotEscalateAClosedTask()
    {
        await using var rig = await Rig.StartAsync();
        var row = await rig.SeedAsync();
        rig.State.Tickets[row.TicketId] = rig.OpenTicket(row) with { IsOpen = false };

        await rig.RunAsync(T0);

        await AssertSkippedAsync(rig, row, "task_closed");
    }

    // ---- the channel ----

    [Fact]
    public async Task EscalatesByWhatsAppWithConnectionApprovedTemplateAndPhone()
    {
        await using var rig = await Rig.StartAsync();
        var row = await rig.SeedAsync();

        await rig.RunAsync(T0);

        var state = await rig.ReadAsync(row.EscalationId);
        Assert.Equal("Whatsapp", state.Status);
        var send = Assert.Single(rig.WhatsApp.Sends);
        Assert.Equal(row.EscalationId, send.ClientId);
        Assert.Equal(row.AssignedBy, send.SentByMemberId);
        Assert.Equal(rig.ConnectionId, send.ConnectionId);
        Assert.Equal("+573001234567", send.PhoneE164);
        Assert.Equal("SOP-000127", send.TicketNumber);
        Assert.Empty(rig.Email.Sent);
        var attempt = Assert.Single(state.ChannelAttempts.EnumerateArray());
        Assert.Equal("whatsapp", attempt.GetProperty("channel").GetString());
        Assert.Equal("sent", attempt.GetProperty("result").GetString());
    }

    [Fact]
    public async Task FallsBackToEmailWithoutPhone()
    {
        await using var rig = await Rig.StartAsync();
        var row = await rig.SeedAsync();
        rig.Recipients.Members[row.MemberId] = rig.Recipient with { PhoneE164 = null };

        await rig.RunAsync(T0);

        var state = await rig.ReadAsync(row.EscalationId);
        Assert.Equal("Email", state.Status);
        Assert.Empty(rig.WhatsApp.Sends);
        var attempt = Assert.Single(state.ChannelAttempts.EnumerateArray());
        Assert.Equal(("email", "sent"), (attempt.GetProperty("channel").GetString(), attempt.GetProperty("result").GetString()));
        Assert.Equal("ana@example.com", Assert.Single(rig.Email.Sent).ToAddress);
    }

    [Fact]
    public async Task FallsBackToEmailWithoutConnection()
    {
        await using var rig = await Rig.StartAsync();
        var row = await rig.SeedAsync();
        rig.State.Tickets[row.TicketId] = rig.OpenTicket(row) with { WhatsAppConnectionId = null };

        await rig.RunAsync(T0);

        Assert.Equal("Email", (await rig.ReadAsync(row.EscalationId)).Status);
        Assert.Empty(rig.WhatsApp.Sends);
        Assert.Single(rig.Email.Sent);
    }

    [Fact]
    public async Task FallsBackToEmailWhenTemplateIsNotApproved()
    {
        await using var rig = await Rig.StartAsync();
        var row = await rig.SeedAsync();
        rig.WhatsApp.Next = new TaskNoticeWhatsAppResult.Failed("template_not_approved");

        await rig.RunAsync(T0);

        var state = await rig.ReadAsync(row.EscalationId);
        Assert.Equal("Email", state.Status);
        var attempts = state.ChannelAttempts.EnumerateArray().Select(item =>
            (item.GetProperty("channel").GetString(), item.GetProperty("result").GetString())).ToList();
        Assert.Equal([("whatsapp", "template_not_approved"), ("email", "sent")], attempts);
    }

    [Fact]
    public async Task FallsBackToEmailWhenMetaFails()
    {
        await using var rig = await Rig.StartAsync();
        var row = await rig.SeedAsync();
        rig.WhatsApp.Next = new TaskNoticeWhatsAppResult.Failed("send_failed:messaging.message.rejected");

        await rig.RunAsync(T0);

        var state = await rig.ReadAsync(row.EscalationId);
        Assert.Equal("Email", state.Status);
        Assert.Equal("send_failed:messaging.message.rejected", state.ChannelAttempts.EnumerateArray().First().GetProperty("result").GetString());
        Assert.Single(rig.Email.Sent);
    }

    [Fact]
    public async Task NeverSendsByTwoChannels()
    {
        await using var rig = await Rig.StartAsync();
        var row = await rig.SeedAsync();

        await rig.RunAsync(T0);
        await rig.RunAsync(T0.AddMinutes(10));

        Assert.Equal("Whatsapp", (await rig.ReadAsync(row.EscalationId)).Status);
        Assert.Single(rig.WhatsApp.Sends);
        Assert.Empty(rig.Email.Sent);
    }

    [Fact]
    public async Task AMemberWithoutEmailFailsTheRow()
    {
        await using var rig = await Rig.StartAsync();
        var row = await rig.SeedAsync();
        rig.Recipients.Members[row.MemberId] = rig.Recipient with { PhoneE164 = null, Email = null };

        await rig.RunAsync(T0);

        var state = await rig.ReadAsync(row.EscalationId);
        Assert.Equal("Failed", state.Status);
        Assert.Equal("no_email", state.ChannelAttempts.EnumerateArray().First().GetProperty("result").GetString());
        Assert.Empty(rig.Email.Sent);
    }

    [Fact]
    public async Task EscalatesACustomerTicketToo()
    {
        await using var rig = await Rig.StartAsync();
        var customer = await rig.SeedAsync(kind: "SupportTicketAssigned");
        rig.State.Tickets[customer.TicketId] = rig.OpenTicket(customer) with { IsInternal = false };

        await rig.RunAsync(T0);

        Assert.Equal("Whatsapp", (await rig.ReadAsync(customer.EscalationId)).Status);

        var byEmail = await rig.SeedAsync(kind: "SupportTicketAssigned");
        rig.State.Tickets[byEmail.TicketId] = rig.OpenTicket(byEmail) with { IsInternal = false, WhatsAppConnectionId = null };
        await rig.RunAsync(T0);

        Assert.Equal("Email", (await rig.ReadAsync(byEmail.EscalationId)).Status);
        Assert.Contains("el ticket", Assert.Single(rig.Email.Sent).TextBody, StringComparison.Ordinal);
    }

    // ---- failures and replicas ----

    [Fact]
    public async Task AFailingRowDoesNotStopTheOthers()
    {
        await using var rig = await Rig.StartAsync();
        var failing = await rig.SeedAsync(tenantId: Guid.CreateVersion7());
        var healthy = await rig.SeedAsync();
        rig.State.ThrowForTenant = failing.TenantId;

        await rig.RunAsync(T0);

        var broken = await rig.ReadAsync(failing.EscalationId);
        Assert.Equal("Pending", broken.Status);
        Assert.Equal(1, broken.Attempts);
        Assert.Equal("Whatsapp", (await rig.ReadAsync(healthy.EscalationId)).Status);
    }

    [Fact]
    public async Task AfterThreeClaimsTheRowFails()
    {
        await using var rig = await Rig.StartAsync();
        var row = await rig.SeedAsync();
        rig.State.ThrowForTenant = row.TenantId;

        // Each run is past the previous lease, so the row is claimed again; the third failed attempt is still an attempt.
        for (var run = 0; run < 3; run++)
        {
            await rig.RunAsync(T0.AddMinutes(run * 3));
        }

        Assert.Equal(3, (await rig.ReadAsync(row.EscalationId)).Attempts);
        await rig.RunAsync(T0.AddMinutes(9));

        var state = await rig.ReadAsync(row.EscalationId);
        Assert.Equal("Failed", state.Status);
        Assert.Equal("attempts_exhausted", state.SkipReason);
        Assert.Empty(rig.WhatsApp.Sends);
    }

    [Fact]
    public async Task TwoConcurrentRunsSendOnce()
    {
        await using var rig = await Rig.StartAsync();
        var row = await rig.SeedAsync();
        rig.WhatsApp.Delay = TimeSpan.FromMilliseconds(300);

        await Task.WhenAll(rig.RunAsync(T0), rig.RunAsync(T0));

        Assert.Single(rig.WhatsApp.Sends);
        Assert.Equal("Whatsapp", (await rig.ReadAsync(row.EscalationId)).Status);
    }

    [Fact]
    public async Task ARowClaimedByAnotherReplicaIsNotTakenUntilTheLeaseEnds()
    {
        await using var rig = await Rig.StartAsync();
        var row = await rig.SeedAsync();
        await rig.ExecuteAsync(
            $"UPDATE notifications.task_notice_escalations SET claimed_until = '{T0.AddMinutes(2):O}', attempts = 1 WHERE id = '{row.EscalationId}'");

        var during = await rig.RunAsync(T0.AddMinutes(1));
        Assert.Equal(0, during);
        Assert.Empty(rig.WhatsApp.Sends);

        var after = await rig.RunAsync(T0.AddMinutes(3));

        Assert.Equal(1, after);
        Assert.Equal("Whatsapp", (await rig.ReadAsync(row.EscalationId)).Status);
    }

    [Fact]
    public async Task ARowTakenOverByAnotherReplicaMidPassIsNotSent()
    {
        await using var rig = await Rig.StartAsync();
        var row = await rig.SeedAsync();
        var otherReplicaLease = T0.AddMinutes(30);
        // This pass's lease expired while it was busy and another replica claimed the row with its own lease.
        rig.State.DuringRead = () => rig.ExecuteAsync(
            $"UPDATE notifications.task_notice_escalations SET claimed_until = '{otherReplicaLease:O}' WHERE id = '{row.EscalationId}'");

        await rig.RunAsync(T0);

        Assert.Empty(rig.WhatsApp.Sends);
        Assert.Empty(rig.Email.Sent);
        var state = await rig.ReadAsync(row.EscalationId);
        Assert.Equal("Pending", state.Status);
        Assert.Equal(otherReplicaLease, state.ClaimedUntil);
    }

    [Fact]
    public async Task ARowFinishedByAnotherReplicaMidPassIsLeftAlone()
    {
        await using var rig = await Rig.StartAsync();
        var row = await rig.SeedAsync();
        rig.State.DuringRead = () => rig.ExecuteAsync(
            $"UPDATE notifications.task_notice_escalations SET status = 'Email', claimed_until = NULL WHERE id = '{row.EscalationId}'");

        await rig.RunAsync(T0);

        Assert.Empty(rig.WhatsApp.Sends);
        Assert.Empty(rig.Email.Sent);
        Assert.Equal("Email", (await rig.ReadAsync(row.EscalationId)).Status);
    }

    [Fact]
    public async Task AWhatsAppThatThrowsFallsBackToEmail()
    {
        await using var rig = await Rig.StartAsync();
        var row = await rig.SeedAsync();
        rig.WhatsApp.Throws = true;

        await rig.RunAsync(T0);

        var state = await rig.ReadAsync(row.EscalationId);
        Assert.Equal("Email", state.Status);
        var attempts = state.ChannelAttempts.EnumerateArray().ToList();
        Assert.Equal("send_failed:exception", attempts[0].GetProperty("result").GetString());
        Assert.Equal("sent", attempts[1].GetProperty("result").GetString());
        Assert.Single(rig.Email.Sent);
    }

    private static async Task AssertSkippedAsync(Rig rig, SeededRow row, string reason)
    {
        var state = await rig.ReadAsync(row.EscalationId);
        Assert.Equal("Skipped", state.Status);
        Assert.Equal(reason, state.SkipReason);
        Assert.Empty(rig.WhatsApp.Sends);
        Assert.Empty(rig.Email.Sent);
    }

    private sealed record SeededRow(Guid EscalationId, Guid EntryId, Guid TenantId, Guid MemberId, Guid AssignedBy, Guid TicketId);

    private sealed record EscalationState(
        string Status, string? SkipReason, int Attempts, DateTimeOffset DueAt, DateTimeOffset? ClaimedUntil, JsonElement ChannelAttempts);

    private sealed class FakeState : ITaskNoticeState
    {
        public ConcurrentDictionary<Guid, TaskNoticeTicket> Tickets { get; } = new();

        public Guid? ThrowForTenant { get; set; }

        /// <summary>Runs while the processor is between its checks and the send: lets a test act as another replica meanwhile.</summary>
        public Func<Task>? DuringRead { get; set; }

        public async Task<TaskNoticeTicket?> FindAsync(Guid tenantId, Guid ticketId, CancellationToken cancellationToken)
        {
            if (tenantId == ThrowForTenant)
            {
                throw new InvalidOperationException("The ticket read failed.");
            }

            if (DuringRead is { } during)
            {
                await during();
            }

            return Tickets.GetValueOrDefault(ticketId);
        }
    }

    private sealed class FakeRecipients : ITaskNoticeRecipients
    {
        private int _calls;

        public ConcurrentDictionary<Guid, TaskNoticeRecipient> Members { get; } = new();

        /// <summary>How many times the processor resolved a recipient: the deferral and the cheap skips must not.</summary>
        public int Calls => Volatile.Read(ref _calls);

        public Task<TaskNoticeRecipient?> FindAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(Members.GetValueOrDefault(memberId));
        }
    }

    private sealed class FakePresence : ITaskNoticePresence
    {
        public bool Connected { get; set; }

        public bool Throws { get; set; }

        public Task<bool> IsConnectedAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken) =>
            Throws ? throw new InvalidOperationException("Presence is down.") : Task.FromResult(Connected);
    }

    private sealed class FakeWhatsApp : ITaskNoticeWhatsApp
    {
        private readonly ConcurrentQueue<TaskNoticeWhatsAppSend> _sends = new();

        public IReadOnlyCollection<TaskNoticeWhatsAppSend> Sends => _sends;

        public TaskNoticeWhatsAppResult Next { get; set; } = new TaskNoticeWhatsAppResult.Sent();

        public TimeSpan Delay { get; set; }

        public bool Throws { get; set; }

        public async Task<TaskNoticeWhatsAppResult> SendAsync(TaskNoticeWhatsAppSend send, CancellationToken cancellationToken)
        {
            _sends.Enqueue(send);
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            return Throws ? throw new InvalidOperationException("Meta is down.") : Next;
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly PostgreSqlContainer _database;
        private readonly NotificationsApiFactory _factory;
        private readonly Guid _tenantId;
        private readonly Guid _ownerMember;

        private Rig(PostgreSqlContainer database, NotificationsApiFactory factory, Guid tenantId, Guid ownerMember, Fakes fakes)
        {
            _database = database;
            _factory = factory;
            _tenantId = tenantId;
            _ownerMember = ownerMember;
            State = fakes.State;
            Recipients = fakes.Recipients;
            Presence = fakes.Presence;
            WhatsApp = fakes.WhatsApp;
            Email = fakes.Email;
        }

        public FakeState State { get; }

        public FakeRecipients Recipients { get; }

        public FakePresence Presence { get; }

        public FakeWhatsApp WhatsApp { get; }

        public RecordingEmailChannel Email { get; }

        public Guid ConnectionId { get; } = Guid.CreateVersion7();

        public TaskNoticeRecipient Recipient { get; } = new(true, "Ana Gómez", "ana@example.com", "+573001234567");

        private string ConnectionString => _database.GetConnectionString();

        public static async Task<Rig> StartAsync()
        {
            var database = await StartDatabaseAsync();
            var fakes = new Fakes(new FakeState(), new FakeRecipients(), new FakePresence(), new FakeWhatsApp(), new RecordingEmailChannel());
            var factory = new NotificationsApiFactory(
                database.GetConnectionString(),
                fakes.Email,
                configureServices: services =>
                {
                    services.RemoveAll<IRealtimeMemberPublisher>();
                    services.AddSingleton<IRealtimeMemberPublisher>(new RecordingMemberPublisher());
                    services.RemoveAll<ITaskNoticeState>();
                    services.AddSingleton<ITaskNoticeState>(fakes.State);
                    services.RemoveAll<ITaskNoticeRecipients>();
                    services.AddSingleton<ITaskNoticeRecipients>(fakes.Recipients);
                    services.RemoveAll<ITaskNoticePresence>();
                    services.AddSingleton<ITaskNoticePresence>(fakes.Presence);
                    services.RemoveAll<ITaskNoticeWhatsApp>();
                    services.AddSingleton<ITaskNoticeWhatsApp>(fakes.WhatsApp);
                    // The hosted worker would claim the rows on its own; the tests drive the processor with their own clock.
                    foreach (var descriptor in services
                        .Where(item => item.ServiceType == typeof(IHostedService) && item.ImplementationType == typeof(TaskNoticeEscalationWorker))
                        .ToList())
                    {
                        services.Remove(descriptor);
                    }
                });
            var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
            var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
            return new Rig(database, factory, tenantId, owner, fakes);
        }

        public TaskNoticeTicket OpenTicket(SeededRow row) =>
            new(true, true, row.MemberId, "SOP-000127", "Cambiar el router", ConnectionId);

        /// <summary>An unread entry with its escalation due a minute before <see cref="T0"/>; the ticket is open, of the member, and the
        /// member is active with phone and email. The assigner is a real member so the email can name them.</summary>
        public async Task<SeededRow> SeedAsync(
            Guid? tenantId = null,
            DateTimeOffset? dueAt = null,
            DateTimeOffset? readAt = null,
            DateTimeOffset? resolvedAt = null,
            string kind = "SupportTaskAssigned")
        {
            var tenant = tenantId ?? _tenantId;
            var member = Guid.CreateVersion7();
            var ticketId = Guid.CreateVersion7();
            var entryId = await InsertEntryAsync(
                ConnectionString, tenant, member, kind,
                JsonSerializer.Serialize(new { ticketId, ticketNumber = "SOP-000127", actorMemberId = _ownerMember }),
                T0.AddMinutes(-31), readAt, ticketId);
            if (resolvedAt is { } resolved)
            {
                await ExecuteAsync($"UPDATE notifications.in_app_notifications SET resolved_at = '{resolved:O}' WHERE id = '{entryId}'");
            }

            var escalationId = Guid.CreateVersion7();
            await using (var connection = new NpgsqlConnection(ConnectionString))
            {
                await connection.OpenAsync(Ct);
                await using var command = new NpgsqlCommand(
                    """
                    INSERT INTO notifications.task_notice_escalations
                        (id, tenant_id, in_app_notification_id, ticket_id, member_id, assigned_by_member_id, due_at, status, created_at)
                    VALUES (@id, @tenant, @entry, @ticket, @member, @by, @due, 'Pending', @created)
                    """,
                    connection);
                command.Parameters.AddWithValue("id", escalationId);
                command.Parameters.AddWithValue("tenant", tenant);
                command.Parameters.AddWithValue("entry", entryId);
                command.Parameters.AddWithValue("ticket", ticketId);
                command.Parameters.AddWithValue("member", member);
                command.Parameters.AddWithValue("by", _ownerMember);
                command.Parameters.AddWithValue("due", dueAt ?? T0.AddMinutes(-1));
                command.Parameters.AddWithValue("created", T0.AddMinutes(-31));
                await command.ExecuteNonQueryAsync(Ct);
            }

            var row = new SeededRow(escalationId, entryId, tenant, member, _ownerMember, ticketId);
            State.Tickets[ticketId] = OpenTicket(row);
            Recipients.Members[member] = Recipient;
            return row;
        }

        /// <summary>One tick of the real worker (several passes, the clock of the host), as opposed to <see cref="RunAsync"/>.</summary>
        public Task TickAsync() =>
            new TaskNoticeEscalationWorker(
                _factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<TaskNoticeEscalationWorker>.Instance)
                .TickAsync(Ct);

        public async Task<int> RunAsync(DateTimeOffset now)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<TaskNoticeEscalationProcessor>().RunAsync(now, Ct);
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(Ct);
        }

        public async Task<EscalationState> ReadAsync(Guid escalationId)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(
                "SELECT status, skip_reason, attempts, due_at, claimed_until, channel_attempts::text FROM notifications.task_notice_escalations WHERE id = @id",
                connection);
            command.Parameters.AddWithValue("id", escalationId);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            Assert.True(await reader.ReadAsync(Ct));
            return new EscalationState(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt32(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                JsonDocument.Parse(reader.GetString(5)).RootElement.Clone());
        }

        public async ValueTask DisposeAsync()
        {
            _factory.Dispose();
            await _database.DisposeAsync();
        }
    }

    private sealed record Fakes(
        FakeState State, FakeRecipients Recipients, FakePresence Presence, FakeWhatsApp WhatsApp, RecordingEmailChannel Email);
}
