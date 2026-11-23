using System.Net;
using Modules.Scheduling.Application;
using static Modules.Scheduling.IntegrationTests.SchedulingApiHarness;

namespace Modules.Scheduling.IntegrationTests;

/// <summary>Las reglas de reserva (plan 2026-11-21, Tarea 7): una fila por tenant con su <c>version</c>; leer es <c>appointment.read</c> y escribir, <c>settings.manage</c>.</summary>
public sealed class SchedulingSettingsApiTests(SchedulingApiWorld world) : IClassFixture<SchedulingApiWorld>
{
    private static object Rules(
        string mode = "Manual", int minNotice = 60, int horizon = 45, int cutoff = 12, bool reminder = false, int offset = 24,
        Guid? connectionId = null, Guid? templateId = null) =>
        new
        {
            confirmationMode = mode,
            minNoticeMinutes = minNotice,
            horizonDays = horizon,
            clientCancelCutoffHours = cutoff,
            reminderEnabled = reminder,
            reminderOffsetHours = offset,
            reminderConnectionId = connectionId,
            reminderTemplateId = templateId,
        };

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
    public async Task TheReminderCannotBeSwitchedOnWithoutItsConnectionAndTemplate()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        await GetJsonAsync(tenant.Admin, $"{tenant.Url}/settings");

        var withoutConnection = await PutAsync(tenant.Admin, $"{tenant.Url}/settings", Rules(reminder: true, templateId: Guid.CreateVersion7()), "1");
        var withoutTemplate = await PutAsync(tenant.Admin, $"{tenant.Url}/settings", Rules(reminder: true, connectionId: Guid.CreateVersion7()), "1");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, withoutConnection.StatusCode);
        Assert.Equal("scheduling.reminder.connection_invalid", CodeOf(await JsonOfAsync(withoutConnection)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, withoutTemplate.StatusCode);
        Assert.Equal("scheduling.reminder.template_missing", CodeOf(await JsonOfAsync(withoutTemplate)));
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
}
