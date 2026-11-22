using System.Net;
using System.Text.Json;
using Modules.Support.Application;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Spec 2026-11-22 §7 and §8.6: the «ticket sin asignar» block of <c>/support/settings</c>. Optional on the PUT (the published frontend does not
/// send it), recipients chosen among those who can manage tickets, a schedule that must be one of the tenant's, and everything the screen
/// needs to draw it on the GET. They share the host: each test uses its own tenant.
/// </summary>
public sealed class SupportUnassignedAlertSettingsApiTests(SharedSupportHost sharedHost) : IClassFixture<SharedSupportHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string SettingsUrl(string tenant) => $"{SupportUrl(tenant)}/settings";

    private static object Alert(bool enabled, int hours, Guid? schedule, params Guid[] recipients) =>
        new { enabled, thresholdHours = hours, scheduleId = schedule, recipientIds = recipients };

    private static JsonElement AlertOf(JsonElement settings) => settings.GetProperty("unassignedAlert");

    [Fact]
    public async Task WithoutTheAlertTheGetShowsItOffWithItsRange()
    {
        var (_, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);

        var alert = AlertOf(await JsonAsync(await client.GetAsync(SettingsUrl(tenant), Ct)));

        Assert.False(alert.GetProperty("enabled").GetBoolean());
        Assert.Equal((4, 1, 72, 20), (
            alert.GetProperty("thresholdHours").GetInt32(),
            alert.GetProperty("minThresholdHours").GetInt32(),
            alert.GetProperty("maxThresholdHours").GetInt32(),
            alert.GetProperty("maxRecipients").GetInt32()));
        Assert.Equal(JsonValueKind.Null, alert.GetProperty("scheduleId").ValueKind);
        Assert.Equal(0, alert.GetProperty("recipients").GetArrayLength());
        Assert.Equal(0, alert.GetProperty("activeRecipientCount").GetInt32());
        Assert.Equal("America/Bogota", alert.GetProperty("timeZone").GetString());
    }

    [Fact]
    public async Task PutWithoutUnassignedAlertKeepsTheStoredOne()
    {
        var (factory, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);
        var ana = await SeedManagerAsync(factory, tenant, "Ana");
        var saved = await PutAsync(
            client, SettingsUrl(tenant), new { dueSoonBusinessDays = 3, unassignedAlert = Alert(true, 8, null, ana) }, version: 1);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        // The published frontend sends only the threshold: that PUT cannot turn the alert off.
        var kept = await PutAsync(client, SettingsUrl(tenant), new { dueSoonBusinessDays = 5 }, version: 2);

        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        var alert = AlertOf(await JsonAsync(kept));
        Assert.True(alert.GetProperty("enabled").GetBoolean());
        Assert.Equal(8, alert.GetProperty("thresholdHours").GetInt32());
        Assert.Equal([ana], alert.GetProperty("recipients").EnumerateArray().Select(item => item.GetProperty("memberId").GetGuid()));
        Assert.Equal(
            1L,
            await ScalarAsync(
                factory,
                "SELECT count(*) FROM audit.entries WHERE action = @action AND resource_id = @tenant AND changed_fields::text = '[\"dueSoonBusinessDays\"]'",
                ("action", SupportAuditActions.SettingsUpdated), ("tenant", tenant)));
    }

    [Fact]
    public async Task SavingTheAlertIsOneVersionAndIsNamedInTheAudit()
    {
        var (factory, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);
        var ana = await SeedManagerAsync(factory, tenant, "Ana");

        var saved = await PutAsync(
            client, SettingsUrl(tenant), new { dueSoonBusinessDays = 3, unassignedAlert = Alert(true, 6, null, ana) }, version: 1);

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(2L, (await JsonAsync(saved)).GetProperty("version").GetInt64());
        Assert.Equal(
            "[\"unassignedAlert\"]",
            await TextAsync(
                factory,
                "SELECT changed_fields::text FROM audit.entries WHERE action = @action AND resource_id = @tenant",
                ("action", SupportAuditActions.SettingsUpdated), ("tenant", tenant)));
        Assert.NotNull(await TextAsync(
            factory,
            "SELECT unassigned_alert_enabled_at::text FROM support.tenant_settings WHERE tenant_id = @tenant",
            ("tenant", Guid.Parse(tenant))));
    }

    [Fact]
    public async Task PutWithANonManagerRecipientIs422AlertRecipientInvalid()
    {
        var (factory, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);
        // The billing role does not grant support.ticket.manage.
        var billing = await SeedMemberAsync(factory, $"billing-{Guid.NewGuid():N}@example.com", "Beatriz", "billing", tenantId: tenant);

        var response = await PutAsync(
            client, SettingsUrl(tenant), new { dueSoonBusinessDays = 3, unassignedAlert = Alert(true, 4, null, billing) }, version: 1);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.settings.alert_recipient_invalid", await CodeOfAsync(response));
        Assert.Equal(1L, (await JsonAsync(await client.GetAsync(SettingsUrl(tenant), Ct))).GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task PutEnabledWithoutRecipientsIs422OnRecipientIds()
    {
        var (_, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);

        var response = await PutAsync(
            client, SettingsUrl(tenant), new { dueSoonBusinessDays = 3, unassignedAlert = Alert(true, 4, null) }, version: 1);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(response));
        Assert.Equal(["UnassignedAlert.RecipientIds"], await ErrorKeysAsync(response));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(73)]
    public async Task ThresholdOutOfRangeIs422OnTheField(int hours)
    {
        var (factory, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);
        var ana = await SeedManagerAsync(factory, tenant, "Ana");

        var response = await PutAsync(
            client, SettingsUrl(tenant), new { dueSoonBusinessDays = 3, unassignedAlert = Alert(true, hours, null, ana) }, version: 1);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(["UnassignedAlert.ThresholdHours"], await ErrorKeysAsync(response));
    }

    [Fact]
    public async Task PutWithAScheduleOfAnotherTenantIs422AlertScheduleInvalid()
    {
        var (factory, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);
        var ana = await SeedManagerAsync(factory, tenant, "Ana");
        var (_, _, otherTenant) = await NewTenantAsync();
        var foreign = await CreateOfficeScheduleAsync(factory, otherTenant, "Ajeno");

        var response = await PutAsync(
            client, SettingsUrl(tenant), new { dueSoonBusinessDays = 3, unassignedAlert = Alert(true, 4, foreign, ana) }, version: 1);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.settings.alert_schedule_invalid", await CodeOfAsync(response));
    }

    [Fact]
    public async Task GetReturnsRecipientsInOrderWithCanReceive()
    {
        var (factory, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);
        var ana = await SeedManagerAsync(factory, tenant, "Ana");
        var bruno = await SeedManagerAsync(factory, tenant, "Bruno");
        var carla = await SeedManagerAsync(factory, tenant, "Carla");
        // Saved as Carla, Ana, Bruno: the screen repaints that order, not the alphabetical one.
        var saved = await PutAsync(
            client, SettingsUrl(tenant), new { dueSoonBusinessDays = 3, unassignedAlert = Alert(true, 4, null, carla, ana, bruno) }, version: 1);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        // Bruno is suspended afterwards: he stays in the list and cannot receive.
        await ExecuteAsync(factory, "UPDATE tenancy.memberships SET state = 'Suspended' WHERE id = @id", ("id", bruno));

        var alert = AlertOf(await JsonAsync(await client.GetAsync(SettingsUrl(tenant), Ct)));

        var recipients = alert.GetProperty("recipients").EnumerateArray().ToList();
        Assert.Equal(
            [("Carla", true), ("Ana", true), ("Bruno", false)],
            recipients.Select(item => (item.GetProperty("name").GetString()!, item.GetProperty("canReceive").GetBoolean())));
        Assert.Equal(2, alert.GetProperty("activeRecipientCount").GetInt32());
        // The options are the people who can manage today, by name.
        Assert.Equal(
            ["Ana", "Carla"],
            alert.GetProperty("recipientOptions").EnumerateArray().Select(item => item.GetProperty("name").GetString()!)
                .Where(name => name is "Ana" or "Bruno" or "Carla").Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task GetReportsNoneWithoutSchedules()
    {
        var (_, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);

        var alert = AlertOf(await JsonAsync(await client.GetAsync(SettingsUrl(tenant), Ct)));

        Assert.Equal(0, alert.GetProperty("scheduleOptions").GetArrayLength());
        var schedule = alert.GetProperty("schedule");
        Assert.Equal("None", schedule.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, schedule.GetProperty("id").ValueKind);
        Assert.Equal(JsonValueKind.Null, schedule.GetProperty("name").ValueKind);
    }

    [Fact]
    public async Task GetReportsTheChosenScheduleAndThenChosenMissingAfterItIsDeleted()
    {
        var (factory, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);
        var ana = await SeedManagerAsync(factory, tenant, "Ana");
        var chosen = await CreateOfficeScheduleAsync(factory, tenant, "Oficina");
        var saved = await PutAsync(
            client, SettingsUrl(tenant), new { dueSoonBusinessDays = 3, unassignedAlert = Alert(true, 4, chosen, ana) }, version: 1);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var before = AlertOf(await JsonAsync(await client.GetAsync(SettingsUrl(tenant), Ct))).GetProperty("schedule");
        Assert.Equal(("Chosen", chosen, "Oficina"), (
            before.GetProperty("source").GetString(), before.GetProperty("id").GetGuid(), before.GetProperty("name").GetString()));

        await DeleteScheduleAsync(factory, tenant, chosen);

        var after = AlertOf(await JsonAsync(await client.GetAsync(SettingsUrl(tenant), Ct)));
        // The stored id stays (it reads as «missing» until someone saves another one); no default exists, so nothing rules.
        Assert.Equal(chosen, after.GetProperty("scheduleId").GetGuid());
        Assert.Equal("ChosenMissing", after.GetProperty("schedule").GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("schedule").GetProperty("id").ValueKind);
    }

    [Fact]
    public async Task ScheduleOptionsNeedOnlySupportSettingsView()
    {
        // The member has support.settings.manage and no messaging.conversation.read: the names still arrive, under Support's permission.
        var (factory, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);
        var office = await CreateOfficeScheduleAsync(factory, tenant, "Oficina");

        var alert = AlertOf(await JsonAsync(await client.GetAsync(SettingsUrl(tenant), Ct)));

        var option = Assert.Single(alert.GetProperty("scheduleOptions").EnumerateArray());
        Assert.Equal((office, "Oficina", true), (option.GetProperty("id").GetGuid(), option.GetProperty("name").GetString(), option.GetProperty("hasWeeklyHours").GetBoolean()));
    }

    private static Task<Guid> SeedManagerAsync(QepApiFactory factory, string tenant, string name) =>
        SeedMemberAsync(factory, $"{name.ToLowerInvariant()}-{Guid.NewGuid():N}@example.com", name, "admin", tenantId: tenant);

    private static async Task DeleteScheduleAsync(QepApiFactory factory, string tenant, Guid scheduleId)
    {
        using var client = CreateClient(
            factory, SubjectId, tenant, "messaging.conversation.read", "messaging.business_hours.manage");
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/tenants/{tenant}/messaging/business-hours/{scheduleId}");
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task<(QepApiFactory Factory, HttpClient Client, string Tenant)> NewTenantAsync(params string[] permissions)
    {
        var factory = await sharedHost.StartTestAsync();
        var tenant = NewTenantId();
        await SeedTenantAsync(factory, tenant);
        return (factory, CreateClient(factory, SubjectId, tenant, permissions), tenant);
    }
}
