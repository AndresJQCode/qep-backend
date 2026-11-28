using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Hr.Application;
using Modules.Hr.Application.BirthdayNotices;
using Npgsql;
using Testcontainers.PostgreSql;
using static Modules.Hr.IntegrationTests.HrApiHarness;
using static Modules.Hr.IntegrationTests.HrApiSeeds;

namespace Modules.Hr.IntegrationTests;

/// <summary>
/// Spec 2026-11-26 §5.1: the birthday notice setting over HTTP. One database for the class and one tenant per test; the WhatsApp
/// connections are a fake port, so the test decides which connection ids "belong" to a tenant.
/// </summary>
public sealed class BirthdayNoticesApiTests : IClassFixture<BirthdayNoticesApiTests.World>
{
    // The last two digits of the id must differ: the tenant slug comes from them.
    private static int _tenantCounter = 0x40;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly World _world;

    public BirthdayNoticesApiTests(World world) => _world = world;

    public sealed class World : IAsyncLifetime
    {
        private PostgreSqlContainer? _database;

        internal FakeWhatsAppConnections Connections { get; } = new();

        internal QepApiFactory Factory { get; private set; } = null!;

        public async ValueTask InitializeAsync()
        {
            _database = await StartDatabaseAsync();
            var connections = Connections;
            Factory = new QepApiFactory(_database.GetConnectionString(), services =>
            {
                services.RemoveAll<IHrWhatsAppConnections>();
                services.AddSingleton<IHrWhatsAppConnections>(connections);
            });
        }

        public async ValueTask DisposeAsync()
        {
            Factory?.Dispose();
            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }
    }

    internal sealed class FakeWhatsAppConnections : IHrWhatsAppConnections
    {
        private readonly HashSet<(Guid TenantId, Guid ConnectionId)> _valid = [];

        private readonly Dictionary<Guid, List<HrWhatsAppConnection>> _listed = [];

        public void Allow(Guid tenantId, Guid connectionId) => _valid.Add((tenantId, connectionId));

        public void List(Guid tenantId, params HrWhatsAppConnection[] connections) => _listed[tenantId] = [.. connections];

        public Task<bool> IsWhatsAppConnectionAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            Task.FromResult(_valid.Contains((tenantId, connectionId)));

        public Task<IReadOnlyList<HrWhatsAppConnection>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HrWhatsAppConnection>>(_listed.TryGetValue(tenantId, out var listed) ? listed : []);
    }

    private sealed class BirthdayTenant(string idText, HttpClient hr, Guid callerMemberId) : IDisposable
    {
        public string IdText { get; } = idText;

        public Guid Id { get; } = Guid.Parse(idText);

        public HttpClient Hr { get; } = hr;

        public Guid CallerMemberId { get; } = callerMemberId;

        public string Url => $"{HrUrl(IdText)}/birthday-notices";

        public void Dispose() => Hr.Dispose();
    }

    private async Task<BirthdayTenant> NewTenantAsync()
    {
        var idText = $"01900000-0000-7000-8000-0000000000{Interlocked.Increment(ref _tenantCounter):x2}";
        await SeedTenantAsync(_world.Factory, idText);
        var caller = await SeedCallerMembershipAsync(_world.Factory, "Quien configura", tenantId: idText);
        var hr = CreateClient(_world.Factory, SubjectId, idText, AllPermissions);
        return new BirthdayTenant(idText, hr, caller);
    }

    private Task<Guid> ManagerAsync(BirthdayTenant tenant, string name, string role = "admin") =>
        SeedMemberAsync(_world.Factory, $"{Guid.NewGuid():N}@example.com", name, role, tenantId: tenant.IdText);

    private static object Body(
        IEnumerable<Guid> managers, int leadDays = 1, bool whatsApp = false, Guid? connection = null, bool email = true) =>
        new { managerMemberIds = managers, leadDays, whatsAppEnabled = whatsApp, whatsAppConnectionId = connection, emailEnabled = email };

    private static async Task<JsonElement> GetAsync(BirthdayTenant tenant)
    {
        var response = await tenant.Hr.GetAsync(tenant.Url, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonOfAsync(response);
    }

    [Fact]
    public async Task GetWithoutRowAnswersDefaults()
    {
        using var tenant = await NewTenantAsync();

        var setting = await GetAsync(tenant);

        Assert.Empty(setting.GetProperty("managers").EnumerateArray());
        Assert.Equal(1, setting.GetProperty("leadDays").GetInt32());
        Assert.False(setting.GetProperty("whatsAppEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, setting.GetProperty("whatsAppConnectionId").ValueKind);
        Assert.False(setting.GetProperty("emailEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, setting.GetProperty("version").ValueKind);
    }

    [Fact]
    public async Task GetListsTheCandidatesAndTheConnectionsEvenWithoutRow()
    {
        using var tenant = await NewTenantAsync();
        var ana = await ManagerAsync(tenant, "Ana Gerente");
        await ManagerAsync(tenant, "Luis Asesor", role: "advisor");
        var connection = Guid.NewGuid();
        _world.Connections.List(
            tenant.Id, new HrWhatsAppConnection(connection, "Ventas", "+57 300 111 2233", HrBirthdayNoticeTemplateStatuses.Pending));

        var setting = await GetAsync(tenant);

        // Only who the PUT accepts as a manager: active and with hr.employee.read. The advisor does not read HR.
        var candidates = setting.GetProperty("candidates").EnumerateArray()
            .Select(candidate => (candidate.GetProperty("memberId").GetGuid(), candidate.GetProperty("displayName").GetString()))
            .ToList();
        Assert.Equal([(ana, "Ana Gerente"), (tenant.CallerMemberId, "Quien configura")], candidates);

        var listed = Assert.Single(setting.GetProperty("connections").EnumerateArray());
        Assert.Equal(connection, listed.GetProperty("connectionId").GetGuid());
        Assert.Equal("Ventas", listed.GetProperty("name").GetString());
        Assert.Equal("+57 300 111 2233", listed.GetProperty("phoneNumber").GetString());
        Assert.Equal("Pending", listed.GetProperty("templateStatus").GetString());
    }

    [Fact]
    public async Task GetWithoutSettingsManageSendsNoCandidatesButTheConnections()
    {
        using var tenant = await NewTenantAsync();
        await ManagerAsync(tenant, "Ana Gerente");
        _world.Connections.List(tenant.Id, new HrWhatsAppConnection(Guid.NewGuid(), "Ventas", null, HrBirthdayNoticeTemplateStatuses.Missing));
        using var reader = CreateClient(_world.Factory, SubjectId, tenant.IdText, HrPermissions.EmployeeRead);

        var response = await reader.GetAsync(tenant.Url, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var setting = await JsonOfAsync(response);
        Assert.Equal(JsonValueKind.Null, setting.GetProperty("candidates").ValueKind);
        var listed = Assert.Single(setting.GetProperty("connections").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("phoneNumber").ValueKind);
        Assert.Equal("Missing", listed.GetProperty("templateStatus").GetString());
    }

    [Fact]
    public async Task PutAnswersTheCandidatesAndTheConnections()
    {
        using var tenant = await NewTenantAsync();
        var ana = await ManagerAsync(tenant, "Ana Gerente");
        _world.Connections.List(tenant.Id, new HrWhatsAppConnection(Guid.NewGuid(), "Ventas", null, HrBirthdayNoticeTemplateStatuses.Approved));

        var response = await PutAsync(tenant.Hr, tenant.Url, Body([ana]), ifMatch: null);

        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        var setting = await JsonOfAsync(response);
        Assert.Contains(setting.GetProperty("candidates").EnumerateArray(), candidate => candidate.GetProperty("memberId").GetGuid() == ana);
        Assert.Equal("Approved", Assert.Single(setting.GetProperty("connections").EnumerateArray()).GetProperty("templateStatus").GetString());
    }

    [Fact]
    public async Task FirstPutCreatesWithoutIfMatch()
    {
        using var tenant = await NewTenantAsync();
        var manager = await ManagerAsync(tenant, "Ana Gerente");

        var response = await PutAsync(tenant.Hr, tenant.Url, Body([manager], leadDays: 2), ifMatch: null);

        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        var setting = await JsonOfAsync(response);
        Assert.Equal(1, setting.GetProperty("version").GetInt64());
        Assert.Equal(2, setting.GetProperty("leadDays").GetInt32());
        var listed = Assert.Single(setting.GetProperty("managers").EnumerateArray());
        Assert.Equal(manager, listed.GetProperty("memberId").GetGuid());
        Assert.Equal("Ana Gerente", listed.GetProperty("displayName").GetString());
        Assert.True(listed.GetProperty("hasHrAccess").GetBoolean());
        Assert.True(listed.GetProperty("hasEmail").GetBoolean());
        Assert.False(listed.GetProperty("hasPhone").GetBoolean());

        // The GET answers the same thing.
        var read = await GetAsync(tenant);
        Assert.Equal(1, read.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task FirstPutWithIfMatchIs412()
    {
        using var tenant = await NewTenantAsync();
        var manager = await ManagerAsync(tenant, "Ana Gerente");

        var response = await PutAsync(tenant.Hr, tenant.Url, Body([manager]), ifMatch: "\"1\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal("concurrency.conflict", CodeOf(await JsonOfAsync(response)));
    }

    [Fact]
    public async Task SecondPutWithoutIfMatchIs428()
    {
        using var tenant = await NewTenantAsync();
        var manager = await ManagerAsync(tenant, "Ana Gerente");
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(tenant.Hr, tenant.Url, Body([manager]), null)).StatusCode);

        var response = await PutAsync(tenant.Hr, tenant.Url, Body([manager]), ifMatch: null);

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
    }

    [Fact]
    public async Task SecondPutWithTheLoadedVersionBumpsIt()
    {
        using var tenant = await NewTenantAsync();
        var manager = await ManagerAsync(tenant, "Ana Gerente");
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(tenant.Hr, tenant.Url, Body([manager]), null)).StatusCode);

        var response = await PutAsync(tenant.Hr, tenant.Url, Body([], leadDays: 0), ifMatch: "\"1\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var setting = await JsonOfAsync(response);
        Assert.Equal(2, setting.GetProperty("version").GetInt64());
        Assert.Empty(setting.GetProperty("managers").EnumerateArray());
    }

    [Fact]
    public async Task StalePutIs412()
    {
        using var tenant = await NewTenantAsync();
        var manager = await ManagerAsync(tenant, "Ana Gerente");
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(tenant.Hr, tenant.Url, Body([manager]), null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(tenant.Hr, tenant.Url, Body([manager], leadDays: 3), "\"1\"")).StatusCode);

        var response = await PutAsync(tenant.Hr, tenant.Url, Body([manager], leadDays: 4), ifMatch: "\"1\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task NullManagerListIs422Validation()
    {
        using var tenant = await NewTenantAsync();

        var response = await PutAsync(
            tenant.Hr, tenant.Url, new { leadDays = 1, whatsAppEnabled = false, emailEnabled = true }, ifMatch: null);

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        var problem = await JsonOfAsync(response);
        Assert.Equal("validation.failed", CodeOf(problem));
        Assert.True(HasError(problem, "managerMemberIds"));
    }

    [Fact]
    public async Task UnknownManagerIs422ManagerNotFound()
    {
        using var tenant = await NewTenantAsync();

        var response = await PutAsync(tenant.Hr, tenant.Url, Body([Guid.NewGuid()]), ifMatch: null);

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        var problem = await JsonOfAsync(response);
        Assert.Equal("hr.birthday_notice.manager_not_found", CodeOf(problem));
        Assert.True(HasError(problem, "managerMemberIds"));
    }

    [Fact]
    public async Task ManagerWithoutHrReadIs422ManagerWithoutAccess()
    {
        using var tenant = await NewTenantAsync();
        var advisor = await ManagerAsync(tenant, "Luis Asesor", role: "advisor");

        var response = await PutAsync(tenant.Hr, tenant.Url, Body([advisor]), ifMatch: null);

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        var problem = await JsonOfAsync(response);
        Assert.Equal("hr.birthday_notice.manager_without_access", CodeOf(problem));
        Assert.True(HasError(problem, "managerMemberIds"));
    }

    [Fact]
    public async Task WhatsAppWithoutConnectionIs422ConnectionRequired()
    {
        using var tenant = await NewTenantAsync();
        var manager = await ManagerAsync(tenant, "Ana Gerente");

        var response = await PutAsync(tenant.Hr, tenant.Url, Body([manager], whatsApp: true, connection: null), ifMatch: null);

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        var problem = await JsonOfAsync(response);
        Assert.Equal("hr.birthday_notice.connection_required", CodeOf(problem));
        Assert.True(HasError(problem, "whatsAppConnectionId"));
    }

    [Fact]
    public async Task ForeignConnectionIs422ConnectionInvalid()
    {
        using var tenant = await NewTenantAsync();
        var manager = await ManagerAsync(tenant, "Ana Gerente");
        var foreign = Guid.NewGuid();
        _world.Connections.Allow(Guid.NewGuid(), foreign);

        var response = await PutAsync(tenant.Hr, tenant.Url, Body([manager], whatsApp: true, connection: foreign), ifMatch: null);

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        var problem = await JsonOfAsync(response);
        Assert.Equal("hr.birthday_notice.connection_invalid", CodeOf(problem));
        Assert.True(HasError(problem, "whatsAppConnectionId"));
    }

    [Fact]
    public async Task OwnConnectionIsAccepted()
    {
        using var tenant = await NewTenantAsync();
        var manager = await ManagerAsync(tenant, "Ana Gerente");
        var connection = Guid.NewGuid();
        _world.Connections.Allow(tenant.Id, connection);

        var response = await PutAsync(tenant.Hr, tenant.Url, Body([manager], whatsApp: true, connection: connection), ifMatch: null);

        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        var setting = await JsonOfAsync(response);
        Assert.True(setting.GetProperty("whatsAppEnabled").GetBoolean());
        Assert.Equal(connection, setting.GetProperty("whatsAppConnectionId").GetGuid());
    }

    [Fact]
    public async Task OtherTenantIs403()
    {
        using var tenant = await NewTenantAsync();
        using var outsider = CreateClient(_world.Factory, OtherSubjectId, OtherTenantId, AllPermissions);

        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(tenant.Url, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(outsider, tenant.Url, Body([]), null)).StatusCode);
    }

    [Fact]
    public async Task PutWithoutSettingsManageIs403()
    {
        using var tenant = await NewTenantAsync();
        using var reader = CreateClient(_world.Factory, SubjectId, tenant.IdText, HrPermissions.EmployeeRead);

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(tenant.Url, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(reader, tenant.Url, Body([]), null)).StatusCode);
    }

    [Fact]
    public async Task GetWithoutEmployeeReadIs403()
    {
        using var tenant = await NewTenantAsync();
        using var nobody = CreateClient(_world.Factory, SubjectId, tenant.IdText, HrPermissions.SettingsManage);

        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync(tenant.Url, Ct)).StatusCode);
    }

    [Fact]
    public async Task PutWritesAudit()
    {
        using var tenant = await NewTenantAsync();
        var manager = await ManagerAsync(tenant, "Ana Gerente");

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(tenant.Hr, tenant.Url, Body([manager]), null)).StatusCode);

        var fields = await AuditedFieldsAsync("hr.birthday_notices.saved", tenant.Id);
        Assert.Contains("managerMemberIds", fields);
        Assert.DoesNotContain(manager.ToString(), fields, StringComparison.OrdinalIgnoreCase);
    }

    // audit.entries has no read API here: read the changed fields of the row straight from the database.
    private async Task<string> AuditedFieldsAsync(string action, Guid tenantId)
    {
        var connectionString = _world.Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT changed_fields::text FROM audit.entries WHERE action = @action AND tenant_id = @tenant AND source = 'hr'",
            connection);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("tenant", tenantId);
        var value = await command.ExecuteScalarAsync(Ct);
        Assert.NotNull(value);
        return (string)value;
    }
}
