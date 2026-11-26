using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Support.Application;
using Modules.Support.Domain;
using Modules.Support.Infrastructure;
using Modules.Support.Infrastructure.Persistence;
using Modules.Support.Infrastructure.Persistence.Migrations;
using Testcontainers.PostgreSql;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;
using static Modules.Support.IntegrationTests.SupportTicketSeed;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Spec 2026-11-22 §7 and D-15/D-16: the worker of the «ticket sin asignar» alert. Own host with the workers off: the test builds the worker
/// and calls <c>RunOnceAsync</c> with the instant it wants, so «hours later» is an argument and not a wait. Without a schedule the tenant counts
/// around the clock; with one, only its ranges.
/// </summary>
public sealed class UnassignedAlertWorkerTests : IAsyncLifetime
{
    private const string AlertEvent = "support.ticket.unassigned-alert.v1";

    // A Monday, 10:00 in Bogota.
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 15, 0, 0, TimeSpan.Zero);

    private PostgreSqlContainer? _database;
    private QepApiFactory? _factory;

    private QepApiFactory Factory => _factory ?? throw new InvalidOperationException("The host is not started.");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => (_database, _factory) = await OpenOwnHostAsync();

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Fact]
    public async Task ATicketPastTheThresholdAlertsOnce()
    {
        var (tenant, recipient) = await ArmedTenantAsync(hours: 4, schedule: null);
        var ticket = await UnassignedTicketAsync(tenant, T0);

        Assert.Equal(0, await RunAsync(T0.AddHours(3)));
        Assert.Equal(0, await EventCountAsync(ticket));

        Assert.Equal(1, await RunAsync(T0.AddHours(5)));
        // The second pass finds the claim of this stretch and writes nothing.
        Assert.Equal(0, await RunAsync(T0.AddHours(6)));

        Assert.Equal(1, await EventCountAsync(ticket));
        var payload = await FirstEventAsync(ticket);
        Assert.Equal(tenant, payload.GetProperty("tenantId").GetGuid());
        Assert.Equal(ticket.Id.Value, payload.GetProperty("ticketId").GetGuid());
        Assert.Equal(ticket.DisplayNumber, payload.GetProperty("ticketNumber").GetString());
        Assert.Equal(4, payload.GetProperty("thresholdHours").GetInt32());
        Assert.Equal([recipient], payload.GetProperty("recipientMemberIds").EnumerateArray().Select(item => item.GetGuid()));
        Assert.Equal(T0, payload.GetProperty("unassignedSince").GetDateTimeOffset());
    }

    [Fact]
    public async Task ATicketAssignedBeforeTheThresholdDoesNotAlert()
    {
        var (tenant, _) = await ArmedTenantAsync(hours: 4, schedule: null);
        var ticket = await UnassignedTicketAsync(tenant, T0);
        await UpdateTicketAsync(Factory, tenant, ticket.Id, value => value.Assign(Guid.CreateVersion7(), Guid.CreateVersion7(), T0.AddHours(2)));

        await RunAsync(T0.AddHours(6));

        Assert.Equal(0, await EventCountAsync(ticket));
    }

    // The worker reads the ticket, counts hours and reads the recipients before it claims: a ticket assigned in that window must get
    // neither a claim nor an event. The claim statement selects from the ticket, so it is the one that refuses.
    [Fact]
    public async Task ATicketAssignedAfterTheCandidateReadGetsNoClaim()
    {
        var (tenant, _) = await ArmedTenantAsync(hours: 4, schedule: null);
        var ticket = await UnassignedTicketAsync(tenant, T0);
        await UpdateTicketAsync(Factory, tenant, ticket.Id, value => value.Assign(Guid.CreateVersion7(), Guid.CreateVersion7(), T0.AddHours(1)));

        bool won;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            won = await scope.ServiceProvider.GetRequiredService<SupportUnassignedAlertStore>()
                .TryClaimAsync(tenant, ticket.Id.Value, T0, 1, T0.AddHours(5), Ct);
        }

        Assert.False(won);
        Assert.Equal(0L, await ScalarAsync(Factory, "SELECT count(*) FROM support.unassigned_alerts WHERE ticket_id = @id", ("id", ticket.Id.Value)));
        Assert.Equal(0, await EventCountAsync(ticket));
    }

    [Fact]
    public async Task UnassigningAgainRestartsTheCounter()
    {
        var (tenant, _) = await ArmedTenantAsync(hours: 4, schedule: null);
        var ticket = await UnassignedTicketAsync(tenant, T0);
        await RunAsync(T0.AddHours(5));
        Assert.Equal(1, await EventCountAsync(ticket));

        var actor = Guid.CreateVersion7();
        await UpdateTicketAsync(Factory, tenant, ticket.Id, value => value.Assign(Guid.CreateVersion7(), actor, T0.AddHours(6)));
        await UpdateTicketAsync(Factory, tenant, ticket.Id, value => value.Assign(null, actor, T0.AddHours(10)));

        // Three hours into the new stretch: not yet. Five: the second alert, with its own claim.
        await RunAsync(T0.AddHours(13));
        Assert.Equal(1, await EventCountAsync(ticket));
        await RunAsync(T0.AddHours(15));
        Assert.Equal(2, await EventCountAsync(ticket));
    }

    [Fact]
    public async Task ATicketThatWaitedOnTheCustomerDoesNotAlertWhenItReopens()
    {
        var (tenant, _) = await ArmedTenantAsync(hours: 4, schedule: null);
        var ticket = await UnassignedTicketAsync(tenant, T0);
        var waiting = await UpdateTicketAsync(
            Factory, tenant, ticket.Id,
            value => value.ChangeStatus(SupportTicketStatus.WaitingOnCustomer, Guid.CreateVersion7(), T0.AddHours(1), Clock));
        Assert.Null(waiting.UnassignedSince);

        // Days on the customer, then ReopenIfWaiting (the system moves it back to Open): the stretch starts at the reopening.
        var reopenedAt = T0.AddDays(5);
        await UpdateTicketAsync(
            Factory, tenant, ticket.Id, value => value.ChangeStatus(SupportTicketStatus.Open, null, reopenedAt, Clock));

        await RunAsync(reopenedAt.AddMinutes(1));
        Assert.Equal(0, await EventCountAsync(ticket));

        await RunAsync(reopenedAt.AddHours(5));
        Assert.Equal(1, await EventCountAsync(ticket));
    }

    [Fact]
    public async Task TheClockStartsWhenTheAlertWasEnabled()
    {
        // Enabled ten hours after the ticket began waiting: the first four business hours count from then.
        var (tenant, _) = await ArmedTenantAsync(hours: 4, schedule: null, enabledAt: T0.AddHours(10));
        var ticket = await UnassignedTicketAsync(tenant, T0);

        await RunAsync(T0.AddHours(12));
        Assert.Equal(0, await EventCountAsync(ticket));

        await RunAsync(T0.AddHours(15));
        Assert.Equal(1, await EventCountAsync(ticket));
    }

    [Fact]
    public async Task InactiveRecipientsAreSkippedAndNoneLeftMeansNoEvent()
    {
        var tenant = await NewTenantAsync();
        var ana = await SeedManagerAsync(tenant, "Ana");
        var bruno = await SeedManagerAsync(tenant, "Bruno");
        await EnableAlertAsync(tenant, hours: 4, schedule: null, [ana, bruno], T0.AddDays(-1));
        await ExecuteAsync(Factory, "UPDATE tenancy.memberships SET state = 'Suspended' WHERE id = @id", ("id", bruno));
        var first = await UnassignedTicketAsync(tenant, T0);

        await RunAsync(T0.AddHours(5));

        Assert.Equal([ana], (await FirstEventAsync(first)).GetProperty("recipientMemberIds").EnumerateArray().Select(item => item.GetGuid()));

        // Nobody left who can receive it: the claim records «nobody to tell» (zero recipients), nothing is published, and it is not retried.
        await ExecuteAsync(Factory, "UPDATE tenancy.memberships SET state = 'Suspended' WHERE id = @id", ("id", ana));
        var second = await UnassignedTicketAsync(tenant, T0.AddHours(6));

        await RunAsync(T0.AddHours(12));
        await RunAsync(T0.AddHours(13));

        Assert.Equal(0, await EventCountAsync(second));
        Assert.Equal(
            1L,
            await ScalarAsync(
                Factory,
                "SELECT count(*) FROM support.unassigned_alerts WHERE ticket_id = @id AND recipient_count = 0",
                ("id", second.Id.Value)));
    }

    [Fact]
    public async Task ANullUnassignedSinceIsHealedWithoutBumpingTheVersion()
    {
        var (tenant, _) = await ArmedTenantAsync(hours: 4, schedule: null);
        var ticket = await UnassignedTicketAsync(tenant, T0);
        // A pod from before the change wrote or moved the ticket without keeping the column.
        await ExecuteAsync(Factory, "UPDATE support.tickets SET unassigned_since = NULL WHERE id = @id", ("id", ticket.Id.Value));
        var before = await FindTicketAsync(Factory, tenant, ticket.Id);
        var now = T0.AddHours(2);

        await RunAsync(now);

        var after = await FindTicketAsync(Factory, tenant, ticket.Id);
        Assert.Equal(now.UtcTicks / 10, after.UnassignedSince!.Value.UtcTicks / 10);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        // It starts counting from the healing, so nothing is due yet.
        Assert.Equal(0, await EventCountAsync(ticket));
    }

    // Rollout: a pod from before the change assigned the ticket without clearing the column. If an old pod unassigned it later, the stale
    // value would survive, so the heal clears the marker of every assigned ticket.
    [Fact]
    public async Task AStaleUnassignedSinceOnAnAssignedTicketIsClearedWithoutBumpingTheVersion()
    {
        var (tenant, _) = await ArmedTenantAsync(hours: 4, schedule: null);
        var ticket = await UnassignedTicketAsync(tenant, T0);
        await UpdateTicketAsync(Factory, tenant, ticket.Id, value => value.Assign(Guid.CreateVersion7(), Guid.CreateVersion7(), T0.AddHours(1)));
        await ExecuteAsync(
            Factory, "UPDATE support.tickets SET unassigned_since = @since WHERE id = @id",
            ("since", T0.UtcDateTime), ("id", ticket.Id.Value));
        var before = await FindTicketAsync(Factory, tenant, ticket.Id);

        await RunAsync(T0.AddHours(6));

        var after = await FindTicketAsync(Factory, tenant, ticket.Id);
        Assert.Null(after.UnassignedSince);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(0, await EventCountAsync(ticket));
    }

    // Merge with internal tasks (spec 2026-11-22 §5.2.1): a task always has an assignee, so it is never «sin asignar». Even a row left without
    // one by hand (raw SQL, a repair) is neither stamped by the heal nor alerted: the alert speaks of customer tickets only.
    [Fact]
    public async Task AnInternalTaskIsNeverHealedNorAlerted()
    {
        var (tenant, recipient) = await ArmedTenantAsync(hours: 4, schedule: null);
        var task = await SaveInternalTicketAsync(Factory, tenant, recipient, recipient, now: T0);
        await ExecuteAsync(
            Factory, "UPDATE support.tickets SET assignee_member_id = NULL, unassigned_since = @since WHERE id = @id",
            ("since", T0.UtcDateTime), ("id", task.Id.Value));

        Assert.Equal(0, await RunAsync(T0.AddHours(6)));
        Assert.Equal(0, await EventCountAsync(task));

        await ExecuteAsync(Factory, "UPDATE support.tickets SET unassigned_since = NULL WHERE id = @id", ("id", task.Id.Value));
        await RunAsync(T0.AddHours(7));
        Assert.False(await HasUnassignedSinceAsync(task));
    }

    [Fact]
    public async Task ADisabledTenantIsSkipped()
    {
        var tenant = await NewTenantAsync();
        var ana = await SeedManagerAsync(tenant, "Ana");
        await EnableAlertAsync(tenant, hours: 4, schedule: null, [ana], T0.AddDays(-1));
        await EnableAlertAsync(tenant, hours: 4, schedule: null, [ana], T0.AddDays(-1), enabled: false);
        var ticket = await UnassignedTicketAsync(tenant, T0);

        Assert.Equal(0, await RunAsync(T0.AddDays(2)));

        Assert.Equal(0, await EventCountAsync(ticket));
    }

    [Fact]
    public async Task ClosedHoursDoNotCount()
    {
        var tenant = await NewTenantAsync();
        var ana = await SeedManagerAsync(tenant, "Ana");
        var office = await CreateOfficeScheduleAsync(Factory, tenant.ToString(), "Oficina");
        await EnableAlertAsync(tenant, hours: 2, office, [ana], T0.AddDays(-7));
        // Friday 17:00 in Bogota (UTC-5, written as the UTC instant Npgsql accepts), one hour before the office closes at 18:00.
        var friday = new DateTimeOffset(2026, 11, 20, 22, 0, 0, TimeSpan.Zero);
        var ticket = await UnassignedTicketAsync(tenant, friday);

        // Friday night, Saturday and Sunday are closed: still one business hour.
        await RunAsync(new DateTimeOffset(2026, 11, 20, 19, 0, 0, TimeSpan.FromHours(-5)));
        await RunAsync(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.FromHours(-5)));
        // Monday 08:30: one and a half hours.
        await RunAsync(new DateTimeOffset(2026, 11, 23, 8, 30, 0, TimeSpan.FromHours(-5)));
        Assert.Equal(0, await EventCountAsync(ticket));

        // Monday 09:00: the second hour is complete.
        await RunAsync(new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.FromHours(-5)));
        Assert.Equal(1, await EventCountAsync(ticket));
    }

    [Fact]
    public async Task WithoutSchedulesHoursCountAroundTheClock()
    {
        var tenant = await NewTenantAsync();
        var ana = await SeedManagerAsync(tenant, "Ana");
        await EnableAlertAsync(tenant, hours: 2, schedule: null, [ana], T0.AddDays(-7));
        var friday = new DateTimeOffset(2026, 11, 20, 22, 0, 0, TimeSpan.Zero);
        var ticket = await UnassignedTicketAsync(tenant, friday);

        await RunAsync(new DateTimeOffset(2026, 11, 20, 19, 1, 0, TimeSpan.FromHours(-5)));

        Assert.Equal(1, await EventCountAsync(ticket));
    }

    // The migration's statement, run as it is: only the tickets that were without an assignee in a status that counts get the stamp.
    [Fact]
    public async Task TheBackfillOnlyStampsCountingStates()
    {
        var tenant = await NewTenantAsync();
        var actor = Guid.CreateVersion7();
        var created = await UnassignedTicketAsync(tenant, T0);
        var open = await UnassignedTicketAsync(tenant, T0);
        var internalWait = await UnassignedTicketAsync(tenant, T0);
        var customerWait = await UnassignedTicketAsync(tenant, T0);
        var resolved = await UnassignedTicketAsync(tenant, T0);
        var assigned = await UnassignedTicketAsync(tenant, T0);
        await UpdateTicketAsync(Factory, tenant, open.Id, value => value.ChangeStatus(SupportTicketStatus.Open, actor, T0.AddHours(1), Clock));
        await UpdateTicketAsync(Factory, tenant, internalWait.Id, value =>
        {
            value.ChangeStatus(SupportTicketStatus.Open, actor, T0.AddHours(1), Clock);
            value.ChangeStatus(SupportTicketStatus.WaitingInternal, actor, T0.AddHours(2), Clock);
        });
        await UpdateTicketAsync(
            Factory, tenant, customerWait.Id, value => value.ChangeStatus(SupportTicketStatus.WaitingOnCustomer, actor, T0.AddHours(1), Clock));
        await UpdateTicketAsync(
            Factory, tenant, resolved.Id, value => value.ChangeStatus(SupportTicketStatus.Resolved, actor, T0.AddHours(1), Clock));
        await UpdateTicketAsync(Factory, tenant, assigned.Id, value => value.Assign(Guid.CreateVersion7(), actor, T0.AddHours(1)));
        // Back to the state the table had before the column existed.
        await ExecuteAsync(Factory, "UPDATE support.tickets SET unassigned_since = NULL WHERE tenant_id = @tenant", ("tenant", tenant));

        await ExecuteAsync(Factory, AddUnassignedAlerts.BackfillSql);

        foreach (var counting in new[] { created, open, internalWait })
        {
            Assert.True(await HasUnassignedSinceAsync(counting), $"{counting.DisplayNumber} should be stamped.");
        }

        foreach (var other in new[] { customerWait, resolved, assigned })
        {
            Assert.False(await HasUnassignedSinceAsync(other), $"{other.DisplayNumber} should stay null.");
        }
    }

    private async Task<Guid> NewTenantAsync()
    {
        var tenant = NewTenantId();
        await SeedTenantAsync(Factory, tenant);
        return Guid.Parse(tenant);
    }

    private Task<Guid> SeedManagerAsync(Guid tenant, string name) =>
        SeedMemberAsync(Factory, $"{name.ToLowerInvariant()}-{Guid.NewGuid():N}@example.com", name, "admin", tenantId: tenant.ToString());

    /// <summary>A tenant with the alert on, one manager as recipient, and the alert enabled long before the tickets of the test.</summary>
    private async Task<(Guid Tenant, Guid Recipient)> ArmedTenantAsync(int hours, Guid? schedule, DateTimeOffset? enabledAt = null)
    {
        var tenant = await NewTenantAsync();
        var recipient = await SeedManagerAsync(tenant, "Ana");
        await EnableAlertAsync(tenant, hours, schedule, [recipient], enabledAt ?? T0.AddDays(-7));
        return (tenant, recipient);
    }

    // Through the aggregate with a chosen instant: the endpoint would stamp the real clock, and the floor of the clock is what is tested.
    private async Task EnableAlertAsync(
        Guid tenant, int hours, Guid? schedule, IReadOnlyList<Guid> recipients, DateTimeOffset enabledAt, bool enabled = true)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await services.GetRequiredService<ISupportProvisioner>().EnsureAsync(tenant, Ct);
        var settings = await services.GetRequiredService<ISupportSettingsRepository>().GetAsync(tenant, Ct);
        settings.Update(
            settings.DueSoonBusinessDays,
            settings.FormalNextStep,
            settings.SurveyEnabled,
            settings.AutoCloseBusinessDays,
            new SupportUnassignedAlertSettings(enabled, hours, schedule, recipients),
            settings.TaskNoticeConnectionId,
            enabledAt);
        await services.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
    }

    private Task<SupportTicket> UnassignedTicketAsync(Guid tenant, DateTimeOffset since) =>
        SaveConversationTicketAsync(Factory, tenant, Guid.CreateVersion7(), since);

    private Task<int> RunAsync(DateTimeOffset now) =>
        ActivatorUtilities.CreateInstance<SupportUnassignedAlertWorker>(Factory.Services).RunOnceAsync(now, Ct);

    private Task<long> EventCountAsync(SupportTicket ticket) =>
        ScalarAsync(
            Factory,
            "SELECT count(*) FROM platform.outbox_messages WHERE event_name = @name AND correlation_id = @id",
            ("name", AlertEvent), ("id", ticket.Id.Value.ToString("D")));

    private async Task<JsonElement> FirstEventAsync(SupportTicket ticket) =>
        JsonDocument.Parse((await TextAsync(
            Factory,
            "SELECT payload::text FROM platform.outbox_messages WHERE event_name = @name AND correlation_id = @id ORDER BY occurred_at, id LIMIT 1",
            ("name", AlertEvent), ("id", ticket.Id.Value.ToString("D"))))!).RootElement;

    private async Task<bool> HasUnassignedSinceAsync(SupportTicket ticket) =>
        await ScalarAsync(
            Factory, "SELECT count(*) FROM support.tickets WHERE id = @id AND unassigned_since IS NOT NULL", ("id", ticket.Id.Value)) == 1;
}
