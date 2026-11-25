using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Siigo.Application;
using static Modules.Siigo.IntegrationTests.SiigoApiHarness;

namespace Modules.Siigo.IntegrationTests;

/// <summary>
/// Spec 2026-11-21: las fallas de las llamadas a Siigo, de punta a punta con el host entero contra Postgres. Las filas se siembran por
/// SQL: la escritura la prueban <c>SiigoApiClientTests</c> y <c>SiigoCallFailureLogTests</c>.
/// </summary>
public sealed class SiigoCallFailuresApiTests
{
    private const string CurlSentinel = "curl.exe -X POST \"https://api.siigo.com/v1/invoices\" SENTINEL-CURL";
    private const string BodySentinel = "SENTINEL-RESPONSE";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Route(Guid tenantId, string suffix = "") => $"/api/v1/tenants/{tenantId}/siigo/call-failures{suffix}";

    private static HttpClient Reader(WebApplicationFactory<Program> factory, RegisteredTenant tenant) =>
        CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, SiigoPermissions.CallsRead);

    private static async Task<(WebApplicationFactory<Program> Factory, RegisteredTenant Tenant, string ConnectionString)> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableSiigoAsync(connectionString, tenant.TenantId);
        return (factory, tenant, connectionString);
    }

    private static async Task<Guid> SeedAsync(
        string connectionString, Guid tenantId, string operation = "InvoiceCreate", int status = 400, int minutesAgo = 5, string? idempotencyKey = "abc123")
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO siigo.call_failures
                (id, tenant_id, connection_id, operation, method, path, status, reason, idempotency_key, request_curl, response_body, trace_id, occurred_at)
            VALUES (@id, @tenant, @tenant, @operation, 'POST', 'v1/invoices', @status, @reason, @key, @curl, @body, 'trace-1', now() - make_interval(mins => @minutes))
            """,
            ("id", id),
            ("tenant", tenantId),
            ("operation", operation),
            ("status", status),
            ("reason", $"http_{status}"),
            ("key", (object?)idempotencyKey ?? DBNull.Value),
            ("curl", CurlSentinel),
            ("body", BodySentinel),
            ("minutes", minutesAgo));
        return id;
    }

    [Fact]
    public async Task TheListShowsTheFailuresWithoutTheCurlOrTheResponse()
    {
        await using var database = await StartDatabaseAsync();
        var (factory, tenant, connectionString) = await ArrangeAsync(database);
        using var _ = factory;
        await SeedAsync(connectionString, tenant.TenantId);
        await SeedAsync(connectionString, tenant.TenantId, "CustomerFind", 422, idempotencyKey: null);
        using var client = Reader(factory, tenant);

        var response = await client.GetAsync(Route(tenant.TenantId), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("SENTINEL", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("requestCurl", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("responseBody", raw, StringComparison.OrdinalIgnoreCase);
        var page = JsonNode.Parse(raw)!;
        Assert.Equal(2, page["total"]!.GetValue<int>());
        Assert.Equal(2, page["items"]!.AsArray().Count);
    }

    [Fact]
    public async Task TheDetailCarriesTheCurlAndTheResponse()
    {
        await using var database = await StartDatabaseAsync();
        var (factory, tenant, connectionString) = await ArrangeAsync(database);
        using var _ = factory;
        var id = await SeedAsync(connectionString, tenant.TenantId);
        using var client = Reader(factory, tenant);

        var response = await client.GetAsync(Route(tenant.TenantId, $"/{id}"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        Assert.Equal(CurlSentinel, detail["requestCurl"]!.GetValue<string>());
        Assert.Equal(BodySentinel, detail["responseBody"]!.GetValue<string>());
        Assert.Equal("InvoiceCreate", detail["operation"]!.GetValue<string>());
        Assert.Equal("abc123", detail["idempotencyKey"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheListFiltersByOperationStatusAndDates()
    {
        await using var database = await StartDatabaseAsync();
        var (factory, tenant, connectionString) = await ArrangeAsync(database);
        using var _ = factory;
        await SeedAsync(connectionString, tenant.TenantId, "InvoiceCreate", 400, minutesAgo: 10);
        await SeedAsync(connectionString, tenant.TenantId, "InvoiceCreate", 502, minutesAgo: 20);
        await SeedAsync(connectionString, tenant.TenantId, "ListRead", 502, minutesAgo: 30);
        using var client = Reader(factory, tenant);

        async Task<int> TotalAsync(string query) =>
            (await client.GetFromJsonAsync<JsonObject>(Route(tenant.TenantId) + query, Ct))!["total"]!.GetValue<int>();

        Assert.Equal(3, await TotalAsync(string.Empty));
        Assert.Equal(2, await TotalAsync("?operation=InvoiceCreate"));
        Assert.Equal(2, await TotalAsync("?status=502"));
        Assert.Equal(1, await TotalAsync("?operation=InvoiceCreate&status=502"));
        Assert.Equal(1, await TotalAsync($"?to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-25).ToString("O"))}"));
        // Hora local de Colombia (-05:00): Npgsql rechaza un offset distinto de cero en timestamptz, así que el repositorio convierte a UTC.
        var colombian = TimeSpan.FromHours(-5);
        Assert.Equal(1, await TotalAsync($"?to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-25).ToOffset(colombian).ToString("O"))}"));
        // Desde hace 25 minutos: las de hace 10 y 20, el complemento exacto del `to` de arriba (con -15 sólo entraba la de hace 10).
        Assert.Equal(2, await TotalAsync($"?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-25).ToOffset(colombian).ToString("O"))}"));
    }

    [Fact]
    public async Task TheListIsNewestFirstAndPaged()
    {
        await using var database = await StartDatabaseAsync();
        var (factory, tenant, connectionString) = await ArrangeAsync(database);
        using var _ = factory;
        var oldest = await SeedAsync(connectionString, tenant.TenantId, minutesAgo: 30);
        var middle = await SeedAsync(connectionString, tenant.TenantId, minutesAgo: 20);
        var newest = await SeedAsync(connectionString, tenant.TenantId, minutesAgo: 10);
        using var client = Reader(factory, tenant);

        var first = (await client.GetFromJsonAsync<JsonObject>(Route(tenant.TenantId) + "?page=1&pageSize=2", Ct))!;
        var second = (await client.GetFromJsonAsync<JsonObject>(Route(tenant.TenantId) + "?page=2&pageSize=2", Ct))!;

        Assert.Equal(3, first["total"]!.GetValue<int>());
        Assert.Equal([newest, middle], first["items"]!.AsArray().Select(item => item!["id"]!.GetValue<Guid>()));
        Assert.Equal([oldest], second["items"]!.AsArray().Select(item => item!["id"]!.GetValue<Guid>()));
    }

    // (page - 1) * pageSize no cabe en un int: la página se recorta en vez de mandar un desplazamiento negativo.
    [Fact]
    public async Task AHugePageIsClampedAndComesBackEmpty()
    {
        await using var database = await StartDatabaseAsync();
        var (factory, tenant, connectionString) = await ArrangeAsync(database);
        using var _ = factory;
        await SeedAsync(connectionString, tenant.TenantId);
        using var client = Reader(factory, tenant);

        var page = (await client.GetFromJsonAsync<JsonObject>(Route(tenant.TenantId) + $"?page={int.MaxValue}&pageSize=100", Ct))!;

        Assert.Empty(page["items"]!.AsArray());
        Assert.Equal((1, int.MaxValue / 100), (page["total"]!.GetValue<int>(), page["page"]!.GetValue<int>()));
    }

    [Theory]
    [InlineData("operation=Nonsense", "operation")]
    [InlineData("operation=3", "operation")]
    [InlineData("operation=Auth,ListRead", "operation")]
    [InlineData("status=abc", "status")]
    [InlineData("status=99", "status")]
    [InlineData("from=ayer", "from")]
    [InlineData("to=ayer", "to")]
    [InlineData("page=x", "page")]
    [InlineData("pageSize=x", "pageSize")]
    public async Task AnUnreadableFilterIs422WithItsField(string query, string field)
    {
        await using var database = await StartDatabaseAsync();
        var (factory, tenant, _) = await ArrangeAsync(database);
        using var __ = factory;
        using var client = Reader(factory, tenant);

        var response = await client.GetAsync(Route(tenant.TenantId) + "?" + query, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["errors"]!.AsObject();
        Assert.Contains(errors, entry => string.Equals(entry.Key, field, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ADateWithoutTimeIsReadAsUtcMidnight()
    {
        await using var database = await StartDatabaseAsync();
        var (factory, tenant, connectionString) = await ArrangeAsync(database);
        using var _ = factory;
        await SeedAsync(connectionString, tenant.TenantId, minutesAgo: 10);
        using var client = Reader(factory, tenant);

        var tomorrow = DateTimeOffset.UtcNow.AddDays(1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var yesterday = DateTimeOffset.UtcNow.AddDays(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(0, (await client.GetFromJsonAsync<JsonObject>(Route(tenant.TenantId) + $"?from={tomorrow}", Ct))!["total"]!.GetValue<int>());
        Assert.Equal(1, (await client.GetFromJsonAsync<JsonObject>(Route(tenant.TenantId) + $"?from={yesterday}", Ct))!["total"]!.GetValue<int>());
    }

    [Fact]
    public async Task AMemberWithoutThePermissionGets403()
    {
        await using var database = await StartDatabaseAsync();
        var (factory, tenant, connectionString) = await ArrangeAsync(database);
        using var _ = factory;
        var id = await SeedAsync(connectionString, tenant.TenantId);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, SiigoPermissions.InvoiceIssue);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Route(tenant.TenantId), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Route(tenant.TenantId, $"/{id}"), Ct)).StatusCode);
    }

    [Fact]
    public async Task AnotherTenantsRouteGets403AndNeverLeaksItsRows()
    {
        await using var database = await StartDatabaseAsync();
        var (factory, tenant, connectionString) = await ArrangeAsync(database);
        using var _ = factory;
        var other = await RegisterTenantAsync(factory);
        await EnableSiigoAsync(connectionString, other.TenantId);
        var foreignId = await SeedAsync(connectionString, other.TenantId);
        using var client = Reader(factory, tenant);

        var list = await client.GetAsync(Route(other.TenantId), Ct);
        var detail = await client.GetAsync(Route(other.TenantId, $"/{foreignId}"), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, detail.StatusCode);
    }

    [Fact]
    public async Task ADetailIdOfAnotherTenantIs404WithItsCode()
    {
        await using var database = await StartDatabaseAsync();
        var (factory, tenant, connectionString) = await ArrangeAsync(database);
        using var _ = factory;
        var other = await RegisterTenantAsync(factory);
        await EnableSiigoAsync(connectionString, other.TenantId);
        var foreignId = await SeedAsync(connectionString, other.TenantId);
        using var client = Reader(factory, tenant);

        var response = await client.GetAsync(Route(tenant.TenantId, $"/{foreignId}"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("siigo.call_failure.not_found", (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["code"]!.GetValue<string>());
    }
}
