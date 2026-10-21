using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-21 §5: el catálogo de horarios de atención con su permiso, la asignación a conexiones (D-H10), el predeterminado
/// (D-H4) y la auditoría. El reloj de la prueba fija «ahora» en miércoles 10:00 en Bogotá.</summary>
public sealed class BusinessHoursApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Wednesday10Bogota = new(2026, 10, 21, 15, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        QepApiFactory Base, WebApplicationFactory<Program> Factory, string ConnectionString, RegisteredTenant Tenant, Guid C1, Guid C2, HttpClient Client, TestClock Clock)
        : IDisposable
    {
        public string Url => BusinessHoursUrl(Tenant.TenantId);

        public string ConnectionsUrl => $"/api/v1/tenants/{Tenant.TenantId}/messaging/connections";

        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
            Base.Dispose();
        }
    }

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var clock = new TestClock { UtcNow = Wednesday10Bogota };
        var baseFactory = new QepApiFactory(connectionString);
        var factory = baseFactory.WithTestClock(clock);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var c1 = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var c2 = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Soporte", "333", "444");
        var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, BusinessHoursPermissions);
        return new Fixture(baseFactory, factory, connectionString, tenant, c1, c2, client, clock);
    }

    private static object Day(int day, params object[] ranges) => new { day, ranges };

    private static object Range(int from, int to) => new { from, to };

    /// <summary>Lunes a viernes 08:00–18:00, sábado y domingo cerrado.</summary>
    private static object[] OfficeWeek() =>
        [Day(1, Range(480, 1080)), Day(2, Range(480, 1080)), Day(3, Range(480, 1080)), Day(4, Range(480, 1080)), Day(5, Range(480, 1080)), Day(6), Day(7)];

    private static object Office(string name = "Oficina", string? holidayCountry = "CO", object[]? exceptions = null, Guid[]? connectionIds = null) =>
        new { name, weekly = OfficeWeek(), exceptions = exceptions ?? [], holidayCountry, connectionIds = connectionIds ?? [] };

    private static async Task<JsonElement> CreateOkAsync(Fixture f, object body)
    {
        var response = await SendAsync(f.Client, HttpMethod.Post, f.Url, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static string IfMatch(JsonElement schedule) => $"\"{schedule.GetProperty("version").GetInt64()}\"";

    private static async Task<JsonElement> ListAsync(Fixture f) => await f.Client.GetFromJsonAsync<JsonElement>(f.Url, Ct);

    private static async Task<JsonElement> ConnectionAsync(Fixture f, Guid id) =>
        (await f.Client.GetFromJsonAsync<JsonElement>(f.ConnectionsUrl, Ct)).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == id);

    private static Task<long> AuditCountAsync(Fixture f, string action) =>
        CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = @a AND tenant_id = @t", ("a", action), ("t", f.Tenant.TenantId));

    [Fact]
    public async Task CreateReturnsTheSevenDaysOpenNowAndNextChange()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);

        var schedule = await CreateOkAsync(f, Office());

        Assert.Equal(7, schedule.GetProperty("weekly").GetArrayLength());
        Assert.True(schedule.GetProperty("openNow").GetBoolean());
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 23, 0, 0, TimeSpan.Zero), schedule.GetProperty("nextChangeAt").GetDateTimeOffset());
        Assert.Equal("CO", schedule.GetProperty("holidayCountry").GetString());
        Assert.Contains(schedule.GetProperty("upcomingHolidays").EnumerateArray(), date => date.GetString() == "2026-11-02");
        Assert.Equal(0, schedule.GetProperty("connections").GetArrayLength());
        Assert.False(schedule.GetProperty("isDefault").GetBoolean());
        Assert.Equal(1L, schedule.GetProperty("version").GetInt64());
        Assert.Equal(1L, await AuditCountAsync(f, "messaging.business_hours.created"));
        Assert.Equal(0L, await AuditCountAsync(f, "messaging.business_hours.connections_assigned"));
    }

    [Fact]
    public async Task TheListBringsTheTimeZoneAndEveryConnectionWithItsSchedule()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await CreateOkAsync(f, Office());

        var list = await ListAsync(f);

        Assert.Equal("America/Bogota", list.GetProperty("timeZone").GetString());
        Assert.Equal(1, list.GetProperty("items").GetArrayLength());
        var connections = list.GetProperty("connections");
        Assert.Equal(2, connections.GetArrayLength());
        Assert.All(connections.EnumerateArray(), connection =>
        {
            Assert.Equal(JsonValueKind.Null, connection.GetProperty("scheduleId").ValueKind);
            Assert.Equal("Active", connection.GetProperty("status").GetString());
            Assert.Equal("WhatsApp", connection.GetProperty("channel").GetString());
        });
    }

    [Fact]
    public async Task ANameInUseIs409AndAnInvalidBodyIs422WithTheField()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await CreateOkAsync(f, Office());

        var taken = await SendAsync(f.Client, HttpMethod.Post, f.Url, Office("  OFICINA "));
        var sixDays = await SendAsync(f.Client, HttpMethod.Post, f.Url, new { name = "Seis", weekly = OfficeWeek().Take(6).ToArray(), exceptions = Array.Empty<object>(), holidayCountry = (string?)null, connectionIds = Array.Empty<Guid>() });
        var overMidnight = await SendAsync(f.Client, HttpMethod.Post, f.Url, new
        {
            name = "Noche",
            weekly = new[] { Day(1, Range(1320, 120)), Day(2), Day(3), Day(4), Day(5), Day(6), Day(7) },
            exceptions = Array.Empty<object>(),
            holidayCountry = (string?)null,
            connectionIds = Array.Empty<Guid>(),
        });
        var us = await SendAsync(f.Client, HttpMethod.Post, f.Url, Office("Gringo", "US"));
        var blank = await SendAsync(f.Client, HttpMethod.Post, f.Url, Office("  "));
        var noBody = await SendAsync(f.Client, HttpMethod.Post, f.Url, new { });

        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal("messaging.business_hours.name_taken", (await ProblemAsync(taken)).Code);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, sixDays.StatusCode);
        Assert.Equal(["Weekly"], (await ProblemAsync(sixDays)).ErrorKeys);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, overMidnight.StatusCode);
        Assert.Equal("validation.failed", (await ProblemAsync(overMidnight)).Code);
        Assert.Equal(["Weekly[0].Ranges[0].To"], (await ProblemAsync(overMidnight)).ErrorKeys);
        Assert.Equal(["HolidayCountry"], (await ProblemAsync(us)).ErrorKeys);
        Assert.Equal(["Name"], (await ProblemAsync(blank)).ErrorKeys);
        Assert.Equal(["ConnectionIds", "Exceptions", "Name", "Weekly"], (await ProblemAsync(noBody)).ErrorKeys);
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.business_hours_schedules"));
    }

    [Fact]
    public async Task UpdateNeedsIfMatchAndReplacesTheConnections()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        var schedule = await CreateOkAsync(f, Office());
        var url = $"{f.Url}/{schedule.GetProperty("id").GetGuid()}";

        var withoutIfMatch = await SendAsync(f.Client, HttpMethod.Put, url, Office(connectionIds: [f.C1]));
        var first = await SendAsync(f.Client, HttpMethod.Put, url, Office(connectionIds: [f.C1]), ifMatch: "\"1\"");
        var stale = await SendAsync(f.Client, HttpMethod.Put, url, Office(connectionIds: [f.C1]), ifMatch: "\"1\"");

        Assert.Equal(HttpStatusCode.PreconditionRequired, withoutIfMatch.StatusCode);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var updated = await first.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(2L, updated.GetProperty("version").GetInt64());
        Assert.Equal(f.C1, Assert.Single(updated.GetProperty("connections").EnumerateArray()).GetProperty("connectionId").GetGuid());
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        var c1 = await ConnectionAsync(f, f.C1);
        Assert.Equal("Oficina", c1.GetProperty("businessHoursName").GetString());
        Assert.True(c1.GetProperty("openNow").GetBoolean());
        Assert.Equal(1L, await AuditCountAsync(f, "messaging.business_hours.updated"));
        Assert.Equal(1L, await AuditCountAsync(f, "messaging.business_hours.connections_assigned"));

        var second = await SendAsync(f.Client, HttpMethod.Put, url, Office(connectionIds: [f.C2]), ifMatch: "\"2\"");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await ConnectionAsync(f, f.C1)).GetProperty("businessHoursScheduleId").ValueKind);
        Assert.Equal("Oficina", (await ConnectionAsync(f, f.C2)).GetProperty("businessHoursName").GetString());
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.connection_business_hours"));
    }

    [Fact]
    public async Task AConnectionOfAnotherTenantIs422OnConnectionIds()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        var other = await RegisterTenantAsync(f.Factory);
        var foreign = await SeedWhatsAppConnectionAsync(f.Factory, other.TenantId, "Ajena", "555", "666");

        var response = await SendAsync(f.Client, HttpMethod.Post, f.Url, Office(connectionIds: [foreign]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(["ConnectionIds"], (await ProblemAsync(response)).ErrorKeys);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.business_hours_schedules"));
    }

    [Fact]
    public async Task APausedConnectionIsAccepted()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        var schedule = await CreateOkAsync(f, Office());
        await ExecuteAsync(f.ConnectionString, "UPDATE integrations.connections SET status = 'Paused' WHERE id = @id", ("id", f.C1));

        var response = await SendAsync(f.Client, HttpMethod.Put, $"{f.Url}/{schedule.GetProperty("id").GetGuid()}", Office(connectionIds: [f.C1]), ifMatch: "\"1\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var option = (await ListAsync(f)).GetProperty("connections").EnumerateArray().Single(c => c.GetProperty("connectionId").GetGuid() == f.C1);
        Assert.Equal("Paused", option.GetProperty("status").GetString());
        Assert.Equal("Oficina", option.GetProperty("scheduleName").GetString());
    }

    [Fact]
    public async Task DuplicateNumbersTheCopies()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        var schedule = await CreateOkAsync(f, Office());
        var url = $"{f.Url}/{schedule.GetProperty("id").GetGuid()}/duplicate";

        var first = await SendAsync(f.Client, HttpMethod.Post, url);
        var second = await SendAsync(f.Client, HttpMethod.Post, url);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal("Oficina (copia)", (await first.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("name").GetString());
        Assert.Equal("Oficina (copia 2)", (await second.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("name").GetString());
        var origin = (await ListAsync(f)).GetProperty("items").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "Oficina");
        Assert.Equal(1L, origin.GetProperty("version").GetInt64());
        Assert.Equal(2L, await AuditCountAsync(f, "messaging.business_hours.duplicated"));
    }

    [Fact]
    public async Task DefaultIsUniqueAndBumpsBothVersions()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        var a = await CreateOkAsync(f, Office("A"));
        var b = await CreateOkAsync(f, Office("B"));
        var aUrl = $"{f.Url}/{a.GetProperty("id").GetGuid()}";
        var bUrl = $"{f.Url}/{b.GetProperty("id").GetGuid()}";
        // D-H4: sin predeterminado ni asignación, la conexión atiende siempre (aquí 19:00 en Bogotá).
        f.Clock.UtcNow = new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
        Assert.True((await ConnectionAsync(f, f.C1)).GetProperty("openNow").GetBoolean());

        var madeA = await SendAsync(f.Client, HttpMethod.Post, $"{aUrl}/default", ifMatch: IfMatch(a));
        var again = await SendAsync(f.Client, HttpMethod.Post, $"{aUrl}/default", ifMatch: "\"2\"");

        Assert.Equal(HttpStatusCode.OK, madeA.StatusCode);
        Assert.Equal(2L, (await madeA.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("version").GetInt64());
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(2L, (await again.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("version").GetInt64());
        // Con A predeterminado y las 19:00 en Bogotá: cerrado.
        Assert.False((await ConnectionAsync(f, f.C1)).GetProperty("openNow").GetBoolean());

        var madeB = await SendAsync(f.Client, HttpMethod.Post, $"{bUrl}/default", ifMatch: IfMatch(b));

        Assert.Equal(HttpStatusCode.OK, madeB.StatusCode);
        var items = (await ListAsync(f)).GetProperty("items").EnumerateArray().ToDictionary(item => item.GetProperty("name").GetString()!);
        Assert.False(items["A"].GetProperty("isDefault").GetBoolean());
        Assert.Equal(3L, items["A"].GetProperty("version").GetInt64());
        Assert.True(items["B"].GetProperty("isDefault").GetBoolean());
        Assert.Equal(2L, items["B"].GetProperty("version").GetInt64());
        Assert.Equal(2L, await AuditCountAsync(f, "messaging.business_hours.default_set"));
    }

    [Fact]
    public async Task DeleteIsRefusedInUseOrDefaultAndWorksOtherwise()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        var inUse = await CreateOkAsync(f, Office("En uso", connectionIds: [f.C1]));
        var isDefault = await CreateOkAsync(f, Office("Predeterminado"));
        var free = await CreateOkAsync(f, Office("Libre"));
        var defaultUrl = $"{f.Url}/{isDefault.GetProperty("id").GetGuid()}";
        var madeDefault = await SendAsync(f.Client, HttpMethod.Post, $"{defaultUrl}/default", ifMatch: IfMatch(isDefault));
        Assert.Equal(HttpStatusCode.OK, madeDefault.StatusCode);

        var inUseResponse = await SendAsync(f.Client, HttpMethod.Delete, $"{f.Url}/{inUse.GetProperty("id").GetGuid()}", ifMatch: IfMatch(inUse));
        var defaultResponse = await SendAsync(f.Client, HttpMethod.Delete, defaultUrl, ifMatch: "\"2\"");
        var freeResponse = await SendAsync(f.Client, HttpMethod.Delete, $"{f.Url}/{free.GetProperty("id").GetGuid()}", ifMatch: IfMatch(free));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, inUseResponse.StatusCode);
        Assert.Equal("messaging.business_hours.in_use", (await ProblemAsync(inUseResponse)).Code);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, defaultResponse.StatusCode);
        Assert.Equal("messaging.business_hours.is_default", (await ProblemAsync(defaultResponse)).Code);
        Assert.Equal(HttpStatusCode.NoContent, freeResponse.StatusCode);
        Assert.DoesNotContain(
            (await ListAsync(f)).GetProperty("items").EnumerateArray(), item => item.GetProperty("name").GetString() == "Libre");
        Assert.Equal(1L, await AuditCountAsync(f, "messaging.business_hours.deleted"));
    }

    [Fact]
    public async Task AnOrphanAssignmentDoesNotCountAsInUseAndIsRemoved()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        var schedule = await CreateOkAsync(f, Office(connectionIds: [f.C1]));
        await ExecuteAsync(f.ConnectionString, "DELETE FROM integrations.connections WHERE id = @id", ("id", f.C1));

        var listed = (await ListAsync(f)).GetProperty("items")[0];
        var response = await SendAsync(f.Client, HttpMethod.Delete, $"{f.Url}/{schedule.GetProperty("id").GetGuid()}", ifMatch: IfMatch(schedule));

        Assert.Equal(0, listed.GetProperty("connections").GetArrayLength());
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.connection_business_hours"));
    }

    [Fact]
    public async Task PastExceptionsArePrunedOnSave()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);

        var schedule = await CreateOkAsync(f, Office(exceptions: [new { date = "2026-01-01", ranges = Array.Empty<object>() }, new { date = "2026-12-25", ranges = new[] { Range(600, 720) } }]));

        var exception = Assert.Single(schedule.GetProperty("exceptions").EnumerateArray());
        Assert.Equal("2026-12-25", exception.GetProperty("date").GetString());
        var listed = Assert.Single((await ListAsync(f)).GetProperty("items").EnumerateArray().Single().GetProperty("exceptions").EnumerateArray());
        Assert.Equal("2026-12-25", listed.GetProperty("date").GetString());
    }

    [Fact]
    public async Task AHolidayClosesTheConnection()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        var schedule = await CreateOkAsync(f, Office(connectionIds: [f.C1]));
        f.Clock.UtcNow = new DateTimeOffset(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);

        Assert.False((await ConnectionAsync(f, f.C1)).GetProperty("openNow").GetBoolean());

        var update = await SendAsync(f.Client, HttpMethod.Put, $"{f.Url}/{schedule.GetProperty("id").GetGuid()}", Office(holidayCountry: null, connectionIds: [f.C1]), ifMatch: "\"1\"");

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.True((await ConnectionAsync(f, f.C1)).GetProperty("openNow").GetBoolean());
    }

    [Fact]
    public async Task ManagingNeedsThePermissionTheTenantAndTheModule()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        var other = await RegisterTenantAsync(f.Factory);
        await EnableMessagingAsync(f.ConnectionString, other.TenantId);
        using var otherManager = CreateClient(f.Factory, other.OwnerUserId, other.TenantId, BusinessHoursPermissions);
        using var reader = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);

        var read = await reader.GetAsync(f.Url, Ct);
        var createWithoutPermission = await SendAsync(reader, HttpMethod.Post, f.Url, Office());
        var otherTenant = await SendAsync(otherManager, HttpMethod.Post, f.Url, Office());
        var otherTenantRead = await otherManager.GetAsync(f.Url, Ct);
        await ExecuteAsync(f.ConnectionString, "UPDATE tenancy.tenant_modules SET status = 'inactive' WHERE tenant_id = @t AND module_key = 'messaging'", ("t", f.Tenant.TenantId));
        var moduleOff = await SendAsync(f.Client, HttpMethod.Post, f.Url, Office());

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, createWithoutPermission.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, otherTenant.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, otherTenantRead.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, moduleOff.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(moduleOff)).Code);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.business_hours_schedules"));
    }

    [Fact]
    public async Task AnUnknownScheduleIs404()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);

        var response = await SendAsync(f.Client, HttpMethod.Put, $"{f.Url}/{Guid.CreateVersion7()}", Office(), ifMatch: "\"1\"");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("messaging.business_hours.not_found", (await ProblemAsync(response)).Code);
    }
}
