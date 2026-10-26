using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Catalog.Application;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Catalog.IntegrationTests;

public sealed class CategoryApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-000000000001";
    private const string SubjectId = "01900000-0000-7000-8000-000000000002";
    private const string OtherTenantId = "01900000-0000-7000-8000-0000000000ff";
    private const string OtherSubjectId = "01900000-0000-7000-8000-0000000000fe";

    private static readonly string[] ManagePermissions =
        [CatalogPermissions.ProductRead, CatalogPermissions.ProductManage];

    [Fact]
    public async Task ListReturnsAnEmptyListForANewTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, CatalogPermissions.ProductRead);

        Assert.Empty(await ListAsync(client, TenantId));
    }

    [Fact]
    public async Task CreateARootReturnsCreatedWithLocationAndListsItWithZeroProducts()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var response = await CreateCategoryAsync(client, TenantId, "  Bebidas ", null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadCategoryAsync(response);
        Assert.Equal(
            $"/api/v1/tenants/{TenantId}/catalog/categories/{created.Id}",
            response.Headers.Location?.ToString());
        Assert.Equal("Bebidas", created.Name);
        Assert.Null(created.ParentId);
        Assert.Equal(0, created.ProductCount);
        Assert.Equal(1, created.Version);
        Assert.Equal(created.CreatedAt, created.UpdatedAt);

        var listed = Assert.Single(await ListAsync(client, TenantId));
        Assert.Equal(created.Id, listed.Id);
        Assert.Equal(0, listed.ProductCount);
    }

    [Fact]
    public async Task CreateAChildCarriesItsParentAndTheListIsFlatSortedByName()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        await CreateCategoryAsync(client, TenantId, "Zapatos", null);
        var calzado = await ReadCategoryAsync(await CreateCategoryAsync(client, TenantId, "Calzado", null));
        await CreateCategoryAsync(client, TenantId, "Botas", calzado.Id);

        var items = await ListAsync(client, TenantId);

        Assert.Equal(["Botas", "Calzado", "Zapatos"], items.Select(item => item.Name));
        Assert.Equal(calzado.Id, items[0].ParentId);
        Assert.Null(items[1].ParentId);
    }

    [Fact]
    public async Task AnUnknownOrForeignParentIsParentNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        using var other = CreateClient(factory, OtherSubjectId, OtherTenantId, ManagePermissions);

        var unknown = await CreateCategoryAsync(client, TenantId, "Hija", Guid.CreateVersion7());
        var foreignRoot = await ReadCategoryAsync(
            await CreateCategoryAsync(other, OtherTenantId, "Ajena", null));
        var foreign = await CreateCategoryAsync(client, TenantId, "Hija", foreignRoot.Id);

        await AssertUnprocessableAsync(unknown, "catalog.category.parent_not_found");
        await AssertUnprocessableAsync(foreign, "catalog.category.parent_not_found");
        Assert.Empty(await ListAsync(client, TenantId));
    }

    [Fact]
    public async Task SiblingNamesClashIgnoringCaseAndEdgeSpaces()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var bebidas = await ReadCategoryAsync(await CreateCategoryAsync(client, TenantId, "Bebidas", null));
        await AssertUnprocessableAsync(
            await CreateCategoryAsync(client, TenantId, "  bebidas ", null),
            "catalog.category.name_taken");

        Assert.Equal(
            HttpStatusCode.Created,
            (await CreateCategoryAsync(client, TenantId, "Gaseosas", bebidas.Id)).StatusCode);
        await AssertUnprocessableAsync(
            await CreateCategoryAsync(client, TenantId, "GASEOSAS", bebidas.Id),
            "catalog.category.name_taken");
    }

    [Fact]
    public async Task TheSameNameUnderAnotherParentIsAccepted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var calzado = await ReadCategoryAsync(await CreateCategoryAsync(client, TenantId, "Calzado", null));
        var ropa = await ReadCategoryAsync(await CreateCategoryAsync(client, TenantId, "Ropa", null));

        Assert.Equal(
            HttpStatusCode.Created,
            (await CreateCategoryAsync(client, TenantId, "Accesorios", calzado.Id)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Created,
            (await CreateCategoryAsync(client, TenantId, "Accesorios", ropa.Id)).StatusCode);
    }

    [Fact]
    public async Task TheTenthLevelIsAllowedAndTheEleventhIsTooDeep()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        Guid? parentId = null;
        for (var level = 1; level <= 10; level++)
        {
            var response = await CreateCategoryAsync(client, TenantId, $"N{level}", parentId);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            parentId = (await ReadCategoryAsync(response)).Id;
        }

        await AssertUnprocessableAsync(
            await CreateCategoryAsync(client, TenantId, "N11", parentId),
            "catalog.category.too_deep");
        Assert.Equal(10, (await ListAsync(client, TenantId)).Count);
    }

    [Fact]
    public async Task CreateWithABlankOrLongNameReturnsTheFieldMap()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        foreach (var name in new[] { "   ", new string('a', 81) })
        {
            var response = await CreateCategoryAsync(client, TenantId, name, null);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("validation.failed", body, StringComparison.Ordinal);
            Assert.Contains("errors", body, StringComparison.Ordinal);
            Assert.Contains("Name", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CreateForAnotherTenantIsForbiddenAndNotNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var intruder = CreateClient(factory, OtherSubjectId, OtherTenantId, ManagePermissions);

        var response = await CreateCategoryAsync(intruder, TenantId, "Bebidas", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ListForAnotherTenantIsForbiddenAndNotNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var intruder = CreateClient(factory, OtherSubjectId, OtherTenantId, CatalogPermissions.ProductRead);

        var response = await intruder.GetAsync(
            $"/api/v1/tenants/{TenantId}/catalog/categories",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ListWithoutTheReadPermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, CatalogPermissions.TaxRateRead);

        var response = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/catalog/categories",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateWithOnlyTheReadPermissionIsForbiddenAndPersistsNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var reader = CreateClient(factory, SubjectId, TenantId, CatalogPermissions.ProductRead);

        var response = await CreateCategoryAsync(reader, TenantId, "Bebidas", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await ListAsync(reader, TenantId));
    }

    [Fact]
    public async Task CreateWritesExactlyOneAuditEventToTheOutbox()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var created = await ReadCategoryAsync(await CreateCategoryAsync(client, TenantId, "Bebidas", null));

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var single = Assert.Single(await QueryAuditEventsAsync(connection, "catalog.category.created"));
        Assert.Contains(TenantId, single, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(SubjectId, single, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(created.Id.ToString(), single, StringComparison.OrdinalIgnoreCase);
    }

    // El conteo del catálogo es el directo e incluye inactivos; el POS cuenta otra cosa.
    [Fact]
    public async Task ProductCountCountsDirectProductsActiveAndInactive()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var bebidas = await ReadCategoryAsync(await CreateCategoryAsync(client, TenantId, "Bebidas", null));
        var gaseosas = await ReadCategoryAsync(await CreateCategoryAsync(client, TenantId, "Gaseosas", bebidas.Id));

        var productA = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            ProductBodies.Create("Cola", "CO-1", categoryId: gaseosas.Id),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, productA.StatusCode);
        var productB = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            ProductBodies.Create("Agua", "AG-1", categoryId: bebidas.Id),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, productB.StatusCode);
        var productBId = (await productB.Content.ReadFromJsonAsync<ProductResponse>(
            TestContext.Current.CancellationToken))!.Id;
        var deactivate = await client.PostAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{productBId}/deactivate",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        var items = await ListAsync(client, TenantId);

        Assert.Equal(1, items.Single(item => item.Id == bebidas.Id).ProductCount);
        Assert.Equal(1, items.Single(item => item.Id == gaseosas.Id).ProductCount);
    }

    private static async Task AssertUnprocessableAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(code, body, StringComparison.Ordinal);
    }

    private static Task<HttpResponseMessage> CreateCategoryAsync(
        HttpClient client,
        string tenantId,
        string name,
        Guid? parentId) =>
        client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/categories",
            new { name, parentId },
            TestContext.Current.CancellationToken);

    private static async Task<CategoryResponse> ReadCategoryAsync(HttpResponseMessage response)
    {
        var category = await response.Content.ReadFromJsonAsync<CategoryResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(category);
        return category;
    }

    private static async Task<IReadOnlyList<CategoryResponse>> ListAsync(HttpClient client, string tenantId)
    {
        var response = await client.GetAsync(
            $"/api/v1/tenants/{tenantId}/catalog/categories",
            TestContext.Current.CancellationToken);

        // El status se assertea acá: un 403 o un 500 deserializan igual con Items en null.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<CategoriesResponse>(
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
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
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
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
        }
    }
}
