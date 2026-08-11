using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Catalog.Application;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Catalog.IntegrationTests;

public sealed class ProductWriteApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-000000000001";
    private const string SubjectId = "01900000-0000-7000-8000-000000000002";
    private const string OtherTenantId = "01900000-0000-7000-8000-0000000000ff";
    private const string OtherSubjectId = "01900000-0000-7000-8000-0000000000fe";

    private static readonly string[] ManagePermissions =
        [CatalogPermissions.ProductRead, CatalogPermissions.ProductManage];

    // CA-CAT-02-04
    [Fact]
    public async Task CreateReturnsCreatedAndTheProductIsReadableAfterwards()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var response = await CreateProductAsync(client, TenantId, "Vela de soja", "VS-001");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadProductAsync(response);
        Assert.True(created.IsActive);
        Assert.Equal("Vela de soja", created.Name);
        Assert.Equal(created.CreatedAt, created.UpdatedAt);

        var fetched = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{created.Id}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
    }

    // CA-CAT-02-04: the audit event has to be in the outbox, committed with the product.
    // Asserted on platform.outbox_messages and not on audit.entries on purpose: catalog uses
    // the outbox path, so audit.entries only appears once the Audit projection worker runs,
    // which would make this assertion a race.
    [Fact]
    public async Task CreateWritesExactlyOneAuditEventToTheOutbox()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var response = await CreateProductAsync(client, TenantId, "Vela de soja", "VS-001");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadProductAsync(response);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var events = await QueryAuditEventsAsync(connection, "catalog.product.created");
        var single = Assert.Single(events);
        Assert.Contains(TenantId, single, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(SubjectId, single, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(created.Id.ToString(), single, StringComparison.OrdinalIgnoreCase);
    }

    // CA-CAT-02-05: the field map comes from the FluentValidation validator, not from the
    // domain exception, which only carries a code.
    [Fact]
    public async Task CreateWithABlankNameReturnsUnprocessableWithTheFieldMap()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var response = await CreateProductAsync(client, TenantId, "   ", "VS-001");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("validation.failed", body, StringComparison.Ordinal);
        Assert.Contains("errors", body, StringComparison.Ordinal);
        Assert.Contains("Name", body, StringComparison.OrdinalIgnoreCase);
    }

    // CA-CAT-02-03: reading is not managing.
    [Fact]
    public async Task CreateWithOnlyTheReadPermissionIsForbiddenAndPersistsNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var reader = CreateClient(
            factory, SubjectId, TenantId, [CatalogPermissions.ProductRead]);

        var response = await CreateProductAsync(reader, TenantId, "Vela de soja", "VS-001");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var list = await reader.GetAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            TestContext.Current.CancellationToken);
        var body = await list.Content.ReadFromJsonAsync<ProductsResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Empty(body.Items);
    }

    // CA-CAT-02-12: the unique violation on IX_products_tenant_code has to surface as a 422
    // with the domain code. Without the translation it is a 500 — the shape of SDD-CT-06.
    [Fact]
    public async Task CreatingTheSameCodeTwiceInATenantReturnsCodeTaken()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var first = await CreateProductAsync(client, TenantId, "Vela de soja", "VS-001");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await CreateProductAsync(client, TenantId, "Otra vela", "VS-001");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("catalog.product.code_taken", body, StringComparison.Ordinal);
    }

    // CA-CAT-02-12, second half: uniqueness is per tenant, not global.
    [Fact]
    public async Task TheSameCodeIsAcceptedInADifferentTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var owner = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        using var other = CreateClient(
            factory, OtherSubjectId, OtherTenantId, ManagePermissions);

        var first = await CreateProductAsync(owner, TenantId, "Vela de soja", "VS-001");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await CreateProductAsync(other, OtherTenantId, "Vela ajena", "VS-001");

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
    }

    // CA-CAT-02-01, the half CAT-02a could not cover: with nothing seeded, an empty list
    // proves nothing about isolation.
    [Fact]
    public async Task ListReturnsOnlyTheProductsOfTheAuthenticatedTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var owner = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        using var other = CreateClient(
            factory, OtherSubjectId, OtherTenantId, ManagePermissions);

        await CreateProductAsync(owner, TenantId, "Vela propia", "VS-001");
        await CreateProductAsync(other, OtherTenantId, "Vela ajena", "VA-001");

        var response = await owner.GetAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<ProductsResponse>(
            TestContext.Current.CancellationToken);

        Assert.NotNull(body);
        var single = Assert.Single(body.Items);
        Assert.Equal("Vela propia", single.Name);
    }

    // CA-CAT-02-10
    [Fact]
    public async Task SearchMatchesNameAndCodeIgnoringCase()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        await CreateProductAsync(client, TenantId, "Vela de soja", "VS-001");
        await CreateProductAsync(client, TenantId, "Difusor de bambú", "DB-002");

        var byName = await ListAsync(client, TenantId, "VELA");
        Assert.Equal("Vela de soja", Assert.Single(byName).Name);

        var byCode = await ListAsync(client, TenantId, "db-0");
        Assert.Equal("Difusor de bambú", Assert.Single(byCode).Name);
    }

    // CA-CAT-02-07
    [Fact]
    public async Task GetUpdateAndDeactivateReturnNotFoundForAnUnknownId()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        var missing = Guid.CreateVersion7();

        var get = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{missing}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);

        var update = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{missing}",
            new { name = "Vela", code = "VS-001" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);

        var deactivate = await client.PostAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{missing}/deactivate",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, deactivate.StatusCode);
    }

    // CA-CAT-02-06
    [Fact]
    public async Task UpdateChangesTheFieldsAndAdvancesUpdatedAt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var created = await ReadProductAsync(
            await CreateProductAsync(client, TenantId, "Vela de soja", "VS-001"));

        var response = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{created.Id}",
            new { name = "Vela de cera", code = "VC-002" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ReadProductAsync(response);
        Assert.Equal("Vela de cera", updated.Name);
        Assert.Equal("VC-002", updated.Code);
        Assert.True(updated.UpdatedAt >= created.UpdatedAt);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Single(await QueryAuditEventsAsync(connection, "catalog.product.updated"));
    }

    // CA-CAT-02-08 and CA-CAT-02-09: inactivating twice is a business error, not a silent
    // success, and it must not reach the database as a 500.
    [Fact]
    public async Task DeactivateTurnsTheProductInactiveAndRejectsASecondAttempt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var created = await ReadProductAsync(
            await CreateProductAsync(client, TenantId, "Vela de soja", "VS-001"));
        var url = $"/api/v1/tenants/{TenantId}/catalog/products/{created.Id}/deactivate";

        var first = await client.PostAsync(
            url, content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.False((await ReadProductAsync(first)).IsActive);

        var second = await client.PostAsync(
            url, content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("catalog.product.already_inactive", body, StringComparison.Ordinal);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Single(await QueryAuditEventsAsync(connection, "catalog.product.deactivated"));
    }

    // CA-CAT-02-11: the permissions are not just constants in code, they are published by
    // the authorization catalogue the UI reads to decide what to render.
    [Fact]
    public async Task CatalogPermissionsArePublishedInTheAuthorizationCatalog()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        // The catalogue endpoint is guarded by tenancy.membership.read, whose definition
        // reads "consultar membresías y catálogo de roles/permisos". Holding the catalog
        // permissions is not enough to read the catalogue that publishes them.
        using var client = CreateClient(
            factory,
            SubjectId,
            TenantId,
            [.. ManagePermissions, "tenancy.membership.read"]);

        var response = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/authorization/catalog",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains(CatalogPermissions.ProductRead, body, StringComparison.Ordinal);
        Assert.Contains(CatalogPermissions.ProductManage, body, StringComparison.Ordinal);
    }

    private static Task<HttpResponseMessage> CreateProductAsync(
        HttpClient client,
        string tenantId,
        string name,
        string code) =>
        client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products",
            new { name, code },
            TestContext.Current.CancellationToken);

    private static async Task<ProductResponse> ReadProductAsync(HttpResponseMessage response)
    {
        var product = await response.Content.ReadFromJsonAsync<ProductResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(product);
        return product;
    }

    private static async Task<IReadOnlyCollection<ProductResponse>> ListAsync(
        HttpClient client,
        string tenantId,
        string? search)
    {
        var query = search is null ? string.Empty : $"?search={Uri.EscapeDataString(search)}";
        var response = await client.GetAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products{query}",
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<ProductsResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body.Items;
    }

    private static async Task<IReadOnlyList<string>> QueryAuditEventsAsync(
        NpgsqlConnection connection,
        string action)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT payload::text
            FROM platform.outbox_messages
            WHERE event_name = 'platform.audit.recorded.v1'
              AND payload->>'action' = @action
            ORDER BY occurred_at
            """,
            connection);
        command.Parameters.AddWithValue("action", action);

        var payloads = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            payloads.Add(reader.GetString(0));
        }

        return payloads;
    }

    private static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }

    private static HttpClient CreateClient(
        QepApiFactory factory,
        string subjectId,
        string tenantId,
        params string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
        }

        return client;
    }

    private sealed class QepApiFactory(string connectionString)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Pinned, never inherited from appsettings.json. SDD-CT-17.
            builder.UseSetting("Notifications:EmailProvider", "log");
        }
    }
}
