using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Scheduling.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using static Modules.Scheduling.IntegrationTests.SchedulingApiHarness;

namespace Modules.Scheduling.IntegrationTests;

/// <summary>Las reglas de reserva (plan 2026-11-21, Tarea 7): una fila por tenant con su <c>version</c>; leer es <c>appointment.read</c> y escribir, <c>settings.manage</c>.</summary>
public sealed class SchedulingSettingsApiTests(SchedulingApiWorld world) : IClassFixture<SchedulingApiWorld>
{
    private static object Rules(
        string mode = "Manual", int minNotice = 60, int horizon = 45, int cutoff = 12, bool reminder = false, int offset = 24,
        Guid? connectionId = null, Guid? senderId = null) =>
        new
        {
            confirmationMode = mode,
            minNoticeMinutes = minNotice,
            horizonDays = horizon,
            clientCancelCutoffHours = cutoff,
            reminderEnabled = reminder,
            reminderOffsetHours = offset,
            reminderConnectionId = connectionId,
            reminderSenderMemberId = senderId,
        };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task SuspendAsync(string tenantId, Guid memberId)
    {
        await using var scope = world.Factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var tenant = await tenancy.Tenants.SingleAsync(value => value.Id == new TenantId(Guid.Parse(tenantId)), Ct);
        var membership = await tenancy.Memberships.SingleAsync(value => value.Id == new MembershipId(memberId), Ct);
        membership.Suspend(tenant, DateTimeOffset.UtcNow);
        await tenancy.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task TheRulesAreEditedWithIfMatch()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var current = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/settings");
        var version = current.GetProperty("version").GetRawText();

        var response = await PutAsync(tenant.Admin, $"{tenant.Url}/settings", Rules(), version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOfAsync(response);
        Assert.Equal("Manual", body.GetProperty("confirmationMode").GetString());
        Assert.Equal(60, body.GetProperty("minNoticeMinutes").GetInt32());
        Assert.Equal(45, body.GetProperty("horizonDays").GetInt32());
        Assert.Equal(12, body.GetProperty("clientCancelCutoffHours").GetInt32());
        Assert.Equal(2, body.GetProperty("version").GetInt64());
        var reread = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/settings");
        Assert.Equal("Manual", reread.GetProperty("confirmationMode").GetString());
    }

    [Fact]
    public async Task AStaleVersionIs412AndAMissingIfMatchIs428()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        await GetJsonAsync(tenant.Admin, $"{tenant.Url}/settings");
        await PutAsync(tenant.Admin, $"{tenant.Url}/settings", Rules(), "1");

        var stale = await PutAsync(tenant.Admin, $"{tenant.Url}/settings", Rules(), "1");
        var missing = await PutAsync(tenant.Admin, $"{tenant.Url}/settings", Rules(), null);

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("concurrency.conflict", CodeOf(await JsonOfAsync(stale)));
        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
    }

    [Fact]
    public async Task ValuesOutOfRangeAre422WithTheirFieldsMarked()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        await GetJsonAsync(tenant.Admin, $"{tenant.Url}/settings");

        var response = await PutAsync(tenant.Admin, $"{tenant.Url}/settings", Rules(mode: "Sometimes", minNotice: -1, horizon: 0, offset: 500), "1");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await JsonOfAsync(response);
        Assert.Equal("validation.failed", CodeOf(problem));
        Assert.True(HasError(problem, "confirmationMode"));
        Assert.True(HasError(problem, "minNoticeMinutes"));
        Assert.True(HasError(problem, "horizonDays"));
        Assert.True(HasError(problem, "reminderOffsetHours"));
    }

    [Fact]
    public async Task TheReminderCannotBeSwitchedOnWithoutItsConnection()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        await GetJsonAsync(tenant.Admin, $"{tenant.Url}/settings");

        var withoutConnection = await PutAsync(tenant.Admin, $"{tenant.Url}/settings", Rules(reminder: true), "1");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, withoutConnection.StatusCode);
        Assert.Equal("scheduling.reminder.connection_invalid", CodeOf(await JsonOfAsync(withoutConnection)));
    }

    [Fact]
    public async Task TheReminderSwitchesOnWithOnlyItsConnectionAndTheTemplateStateIsResolved()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var initial = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/settings");
        var connection = Guid.CreateVersion7();

        var response = await PutAsync(tenant.Admin, $"{tenant.Url}/settings", Rules(reminder: true, connectionId: connection), "1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOfAsync(response);
        Assert.True(body.GetProperty("reminderEnabled").GetBoolean());
        Assert.Equal(connection, body.GetProperty("reminderConnectionId").GetGuid());
        Assert.False(body.TryGetProperty("reminderTemplateId", out _));
        // No AppointmentReminder row exists for that connection, so the state is Missing.
        var state = body.GetProperty("reminderTemplate");
        Assert.Equal("Missing", state.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("statusReason").ValueKind);
        var reread = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/settings");
        Assert.Equal("Missing", reread.GetProperty("reminderTemplate").GetProperty("status").GetString());
        // Without a saved connection there is nothing to look up.
        Assert.Equal(JsonValueKind.Null, initial.GetProperty("reminderTemplate").ValueKind);
    }

    [Fact]
    public async Task SwitchingTheReminderOffClearsTheConnectionAndTheTemplateState()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        await GetJsonAsync(tenant.Admin, $"{tenant.Url}/settings");
        (await PutAsync(tenant.Admin, $"{tenant.Url}/settings", Rules(reminder: true, connectionId: Guid.CreateVersion7()), "1"))
            .EnsureSuccessStatusCode();

        var off = await JsonOfAsync(await PutAsync(tenant.Admin, $"{tenant.Url}/settings", Rules(connectionId: Guid.CreateVersion7()), "2"));

        Assert.False(off.GetProperty("reminderEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, off.GetProperty("reminderConnectionId").ValueKind);
        Assert.Equal(JsonValueKind.Null, off.GetProperty("reminderTemplate").ValueKind);
    }

    [Fact]
    public async Task AnOldClientThatStillSendsReminderTemplateIdIsNotRejected()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        await GetJsonAsync(tenant.Admin, $"{tenant.Url}/settings");
        var connection = Guid.CreateVersion7();
        var legacy = new
        {
            confirmationMode = "Manual",
            minNoticeMinutes = 60,
            horizonDays = 45,
            clientCancelCutoffHours = 12,
            reminderEnabled = true,
            reminderOffsetHours = 24,
            reminderConnectionId = connection,
            reminderTemplateId = Guid.CreateVersion7(),
        };

        var response = await PutAsync(tenant.Admin, $"{tenant.Url}/settings", legacy, "1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOfAsync(response);
        Assert.Equal(connection, body.GetProperty("reminderConnectionId").GetGuid());
        Assert.False(body.TryGetProperty("reminderTemplateId", out _));
    }

    [Fact]
    public async Task WithoutSettingsManageTheRulesCanBeReadButNotWritten()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        using var manager = tenant.Client(
            null, SchedulingPermissions.AppointmentRead, SchedulingPermissions.AppointmentManage, SchedulingPermissions.AvailabilityManage);
        var current = await GetJsonAsync(manager, $"{tenant.Url}/settings");

        var write = await PutAsync(manager, $"{tenant.Url}/settings", Rules(), current.GetProperty("version").GetRawText());

        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        var unchanged = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/settings");
        Assert.Equal("Automatic", unchanged.GetProperty("confirmationMode").GetString());
    }

    [Fact]
    public async Task ReminderSenderRoundTripsWithItsName()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var (memberId, _) = await SeedMemberAsync(world.Factory, tenant.IdText, "ana@example.com", "Ana Gómez");
        await GetJsonAsync(tenant.Admin, $"{tenant.Url}/settings");

        var response = await PutAsync(tenant.Admin, $"{tenant.Url}/settings", Rules(senderId: memberId), "1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reread = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/settings");
        Assert.Equal(memberId, reread.GetProperty("reminderSenderMemberId").GetGuid());
        Assert.Equal("Ana Gómez", reread.GetProperty("reminderSenderMemberName").GetString());
    }

    [Fact]
    public async Task ReminderSenderIsStoredWithTheReminderOff()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var (memberId, _) = await SeedMemberAsync(world.Factory, tenant.IdText, "ana@example.com", "Ana Gómez");
        await GetJsonAsync(tenant.Admin, $"{tenant.Url}/settings");

        var body = await JsonOfAsync(await PutAsync(tenant.Admin, $"{tenant.Url}/settings", Rules(reminder: false, senderId: memberId), "1"));

        Assert.False(body.GetProperty("reminderEnabled").GetBoolean());
        Assert.Equal(memberId, body.GetProperty("reminderSenderMemberId").GetGuid());
    }

    [Fact]
    public async Task ASuspendedSenderIsStillNamed()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var (memberId, _) = await SeedMemberAsync(world.Factory, tenant.IdText, "ana@example.com", "Ana Gómez");
        await GetJsonAsync(tenant.Admin, $"{tenant.Url}/settings");
        (await PutAsync(tenant.Admin, $"{tenant.Url}/settings", Rules(senderId: memberId), "1")).EnsureSuccessStatusCode();

        await SuspendAsync(tenant.IdText, memberId);

        var reread = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/settings");
        Assert.Equal(memberId, reread.GetProperty("reminderSenderMemberId").GetGuid());
        Assert.Equal("Ana Gómez", reread.GetProperty("reminderSenderMemberName").GetString());
    }

    [Fact]
    public async Task AnInactiveSenderIs422WithItsField()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var (memberId, _) = await SeedMemberAsync(world.Factory, tenant.IdText, "ana@example.com", "Ana Gómez");
        await SuspendAsync(tenant.IdText, memberId);
        await GetJsonAsync(tenant.Admin, $"{tenant.Url}/settings");

        var response = await PutAsync(tenant.Admin, $"{tenant.Url}/settings", Rules(senderId: memberId), "1");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await JsonOfAsync(response);
        Assert.Equal("validation.failed", CodeOf(problem));
        Assert.True(HasError(problem, "reminderSenderMemberId"));
    }

    [Fact]
    public async Task ASenderFromAnotherTenantIs422WithItsField()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        using var other = await NewTenantAsync(world.Factory, "Otra clínica");
        var (foreignMember, _) = await SeedMemberAsync(world.Factory, other.IdText, "luis@example.com", "Luis Mora");
        await GetJsonAsync(tenant.Admin, $"{tenant.Url}/settings");

        var response = await PutAsync(tenant.Admin, $"{tenant.Url}/settings", Rules(senderId: foreignMember), "1");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True(HasError(await JsonOfAsync(response), "reminderSenderMemberId"));
    }

    [Fact]
    public async Task NullClearsTheSender()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var (memberId, _) = await SeedMemberAsync(world.Factory, tenant.IdText, "ana@example.com", "Ana Gómez");
        await GetJsonAsync(tenant.Admin, $"{tenant.Url}/settings");
        (await PutAsync(tenant.Admin, $"{tenant.Url}/settings", Rules(senderId: memberId), "1")).EnsureSuccessStatusCode();

        (await PutAsync(tenant.Admin, $"{tenant.Url}/settings", Rules(), "2")).EnsureSuccessStatusCode();

        var reread = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/settings");
        Assert.Equal(JsonValueKind.Null, reread.GetProperty("reminderSenderMemberId").ValueKind);
        Assert.Equal(JsonValueKind.Null, reread.GetProperty("reminderSenderMemberName").ValueKind);
    }

    [Fact]
    public async Task ChangingTheSenderIsAudited()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var (memberId, _) = await SeedMemberAsync(world.Factory, tenant.IdText, "ana@example.com", "Ana Gómez");
        await GetJsonAsync(tenant.Admin, $"{tenant.Url}/settings");

        (await PutAsync(tenant.Admin, $"{tenant.Url}/settings", Rules(senderId: memberId), "1")).EnsureSuccessStatusCode();

        var trail = await ReadAuditFieldsAsync(
            world.Factory, tenant.IdText, SchedulingAuditActions.SettingsUpdated, SchedulingAuditActions.Resources.Settings, tenant.Id);
        Assert.Contains("reminderSenderMemberId", Assert.Single(trail));
    }

    // Block 1 review: a sender that was suspended after it was saved must not block an unrelated edit of the rules; only a *changed* id is
    // validated. Otherwise the whole form is stuck on a field the person did not touch.
    [Fact]
    public async Task ASuspendedSenderDoesNotBlockAnUnrelatedEdit()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var (memberId, _) = await SeedMemberAsync(world.Factory, tenant.IdText, "ana@example.com", "Ana Gómez");
        await GetJsonAsync(tenant.Admin, $"{tenant.Url}/settings");
        (await PutAsync(tenant.Admin, $"{tenant.Url}/settings", Rules(senderId: memberId), "1")).EnsureSuccessStatusCode();
        await SuspendAsync(tenant.IdText, memberId);

        var response = await PutAsync(tenant.Admin, $"{tenant.Url}/settings", Rules(senderId: memberId, cutoff: 6), "2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOfAsync(response);
        Assert.Equal((6, memberId), (body.GetProperty("clientCancelCutoffHours").GetInt32(), body.GetProperty("reminderSenderMemberId").GetGuid()));
    }

    [Fact]
    public async Task ChangingToAnotherSuspendedSenderIsStill422()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var (first, _) = await SeedMemberAsync(world.Factory, tenant.IdText, "ana@example.com", "Ana Gómez");
        var (second, _) = await SeedMemberAsync(world.Factory, tenant.IdText, "luis@example.com", "Luis Mora");
        await GetJsonAsync(tenant.Admin, $"{tenant.Url}/settings");
        (await PutAsync(tenant.Admin, $"{tenant.Url}/settings", Rules(senderId: first), "1")).EnsureSuccessStatusCode();
        await SuspendAsync(tenant.IdText, first);
        await SuspendAsync(tenant.IdText, second);

        var response = await PutAsync(tenant.Admin, $"{tenant.Url}/settings", Rules(senderId: second), "2");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True(HasError(await JsonOfAsync(response), "reminderSenderMemberId"));
    }
}
