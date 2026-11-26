using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Support.Application;
using Npgsql;
using Testcontainers.PostgreSql;
using static Modules.Support.IntegrationTests.SupportApiHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Task-notices spec 2026-11-22 §5.4: the <c>taskNotice</c> block of <c>/support/settings</c>. Optional on the PUT (the published frontend does
/// not send it), the connection must be one the tenant lists, and a chosen one that is no longer listed stays stored. The WhatsApp connections
/// are Integrations' and Messaging's, so the host replaces <see cref="ISupportTaskNoticeChannels"/> with a double each test can change.
/// </summary>
public sealed class SupportTaskNoticeSettingsApiTests : IAsyncLifetime
{
    private readonly StubChannels _channels = new();
    private PostgreSqlContainer? _database;
    private QepApiFactory? _factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string SettingsUrl(string tenant) => $"{SupportUrl(tenant)}/settings";

    public async ValueTask InitializeAsync()
    {
        (_database, _factory) = await OpenOwnHostAsync(services =>
        {
            services.RemoveAll<ISupportTaskNoticeChannels>();
            services.AddSingleton<ISupportTaskNoticeChannels>(_channels);
        });
    }

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

    private async Task<(HttpClient Client, string Tenant)> NewTenantAsync()
    {
        var tenant = NewTenantId();
        await SeedTenantAsync(_factory!, tenant);
        return (CreateClient(_factory!, SubjectId, tenant, SupportPermissions.SettingsManage), tenant);
    }

    private static JsonElement NoticeOf(JsonElement settings) => settings.GetProperty("taskNotice");

    [Fact]
    public async Task GetReturnsNullConnectionAndTheOptionsByDefault()
    {
        var (client, tenant) = await NewTenantAsync();
        var ventas = _channels.Add("Ventas", "+57 300 123 4567", SupportTaskNoticeTemplateStatuses.Pending);

        var notice = NoticeOf(await JsonAsync(await client.GetAsync(SettingsUrl(tenant), Ct)));

        Assert.Equal(JsonValueKind.Null, notice.GetProperty("whatsAppConnectionId").ValueKind);
        Assert.Equal(30, notice.GetProperty("escalationAfterMinutes").GetInt32());
        var connection = Assert.Single(notice.GetProperty("connections").EnumerateArray());
        Assert.Equal(ventas, connection.GetProperty("connectionId").GetGuid());
        Assert.Equal("Pending", connection.GetProperty("templateStatus").GetString());
        Assert.Equal("+57 300 123 4567", connection.GetProperty("phoneNumber").GetString());
    }

    [Fact]
    public async Task PutSavesAConnectionWithoutAnApprovedTemplate()
    {
        var (client, tenant) = await NewTenantAsync();
        var ventas = _channels.Add("Ventas", "+57 300 123 4567", SupportTaskNoticeTemplateStatuses.Pending);

        var response = await PutAsync(
            client, SettingsUrl(tenant), new { dueSoonBusinessDays = 3, taskNotice = new { whatsAppConnectionId = ventas } }, version: 1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ventas, NoticeOf(await JsonAsync(response)).GetProperty("whatsAppConnectionId").GetGuid());
    }

    [Fact]
    public async Task PutRejectsAConnectionThatIsNotListed()
    {
        var (client, tenant) = await NewTenantAsync();

        var response = await PutAsync(
            client,
            SettingsUrl(tenant),
            new { dueSoonBusinessDays = 3, taskNotice = new { whatsAppConnectionId = Guid.CreateVersion7() } },
            version: 1);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.settings.task_notice_connection_invalid", await CodeOfAsync(response));
    }

    [Fact]
    public async Task PutWithoutTaskNoticeKeepsTheConnection()
    {
        var (client, tenant) = await NewTenantAsync();
        var ventas = _channels.Add("Ventas", null, SupportTaskNoticeTemplateStatuses.Approved);
        await PutAsync(client, SettingsUrl(tenant), new { dueSoonBusinessDays = 3, taskNotice = new { whatsAppConnectionId = ventas } }, version: 1);

        var kept = await PutAsync(client, SettingsUrl(tenant), new { dueSoonBusinessDays = 5 }, version: 2);

        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal(ventas, NoticeOf(await JsonAsync(kept)).GetProperty("whatsAppConnectionId").GetGuid());
    }

    [Fact]
    public async Task PutWithNullTaskNoticeKeepsTheConnection()
    {
        var (client, tenant) = await NewTenantAsync();
        var ventas = _channels.Add("Ventas", null, SupportTaskNoticeTemplateStatuses.Approved);
        await PutAsync(client, SettingsUrl(tenant), new { dueSoonBusinessDays = 3, taskNotice = new { whatsAppConnectionId = ventas } }, version: 1);

        var kept = await PutAsync(client, SettingsUrl(tenant), new { dueSoonBusinessDays = 3, taskNotice = (object?)null }, version: 2);

        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal(ventas, NoticeOf(await JsonAsync(kept)).GetProperty("whatsAppConnectionId").GetGuid());
    }

    [Fact]
    public async Task PutWithNullConnectionClearsIt()
    {
        var (client, tenant) = await NewTenantAsync();
        var ventas = _channels.Add("Ventas", null, SupportTaskNoticeTemplateStatuses.Approved);
        await PutAsync(client, SettingsUrl(tenant), new { dueSoonBusinessDays = 3, taskNotice = new { whatsAppConnectionId = ventas } }, version: 1);

        var cleared = await PutAsync(
            client, SettingsUrl(tenant), new { dueSoonBusinessDays = 3, taskNotice = new { whatsAppConnectionId = (Guid?)null } }, version: 2);

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal(JsonValueKind.Null, NoticeOf(await JsonAsync(cleared)).GetProperty("whatsAppConnectionId").ValueKind);
    }

    // Review focus 5: a connection that was paused or lost its template is not deleted from the settings by itself.
    [Fact]
    public async Task TheChosenConnectionSurvivesWhenItIsNoLongerListed()
    {
        var (client, tenant) = await NewTenantAsync();
        var ventas = _channels.Add("Ventas", null, SupportTaskNoticeTemplateStatuses.Approved);
        await PutAsync(client, SettingsUrl(tenant), new { dueSoonBusinessDays = 3, taskNotice = new { whatsAppConnectionId = ventas } }, version: 1);

        _channels.Clear();
        var notice = NoticeOf(await JsonAsync(await client.GetAsync(SettingsUrl(tenant), Ct)));

        Assert.Equal(ventas, notice.GetProperty("whatsAppConnectionId").GetGuid());
        Assert.Equal(0, notice.GetProperty("connections").GetArrayLength());
    }

    [Fact]
    public async Task ChangeIsAuditedAsTaskNotice()
    {
        var (client, tenant) = await NewTenantAsync();
        var ventas = _channels.Add("Ventas", null, SupportTaskNoticeTemplateStatuses.Approved);

        await PutAsync(client, SettingsUrl(tenant), new { dueSoonBusinessDays = 3, taskNotice = new { whatsAppConnectionId = ventas } }, version: 1);

        var connectionString = _factory!.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT changed_fields::text FROM audit.entries WHERE action = @action AND resource_id = @tenant", connection);
        command.Parameters.AddWithValue("action", SupportAuditActions.SettingsUpdated);
        command.Parameters.AddWithValue("tenant", tenant);
        Assert.Equal("[\"taskNotice\"]", (string?)await command.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task StaleIfMatchIs412()
    {
        var (client, tenant) = await NewTenantAsync();
        var ventas = _channels.Add("Ventas", null, SupportTaskNoticeTemplateStatuses.Approved);

        var response = await PutAsync(
            client, SettingsUrl(tenant), new { dueSoonBusinessDays = 3, taskNotice = new { whatsAppConnectionId = ventas } }, version: 7);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    private sealed class StubChannels : ISupportTaskNoticeChannels
    {
        private readonly List<SupportTaskNoticeConnection> _connections = [];

        public int EscalationAfterMinutes => 30;

        public Guid Add(string name, string? phone, string templateStatus)
        {
            var id = Guid.CreateVersion7();
            _connections.Add(new SupportTaskNoticeConnection(id, name, phone, templateStatus));
            return id;
        }

        public void Clear() => _connections.Clear();

        public Task<IReadOnlyList<SupportTaskNoticeConnection>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SupportTaskNoticeConnection>>([.. _connections]);
    }
}
