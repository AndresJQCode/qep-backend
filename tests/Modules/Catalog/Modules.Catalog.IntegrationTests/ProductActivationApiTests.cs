using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Catalog.Application;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// CAT-07 — reactivación de producto.
///
/// Lo que este archivo verifica no es «un endpoint más»: es que el estado inactivo deje de ser
/// terminal. Hasta este slice, <c>Update</c> abría con <c>EnsureActive()</c> y ningún método
/// devolvía <c>IsActive</c> a <c>true</c>, así que la única salida de un producto desactivado era
/// un <c>UPDATE</c> por SQL. Por eso CA-CAT-07-03 no se conforma con el 200 de la activación y
/// sigue con un <c>PUT</c>.
/// </summary>
public sealed class ProductActivationApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-000000000031";
    private const string SubjectId = "01900000-0000-7000-8000-000000000032";
    private const string OtherTenantId = "01900000-0000-7000-8000-0000000000cc";
    private const string OtherSubjectId = "01900000-0000-7000-8000-0000000000cb";

    private static readonly string[] ManagePermissions =
        [CatalogPermissions.ProductRead, CatalogPermissions.ProductManage];

    private static readonly string[] ReadOnlyPermissions =
        [CatalogPermissions.ProductRead];

    // CA-CAT-07-01
    [Fact]
    public async Task ActivateBringsAnInactiveProductBackAndPersistsIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var created = await ReadProductAsync(
            await CreateProductAsync(client, TenantId, "Vela de soja", "VS-001"));
        await DeactivateAsync(client, TenantId, created.Id);

        var response = await ActivateAsync(client, TenantId, created.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await ReadProductAsync(response)).IsActive);

        // El 200 no alcanza: lo que importa es la fila. Un handler que devuelva el DTO sin
        // guardar pasaría la aserción de arriba y fallaría ésta.
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.True(await QueryIsActiveAsync(connection, created.Id));
    }

    // CA-CAT-07-02: activar algo ya activo es un error de negocio. Ni 200 silencioso, ni 500.
    [Fact]
    public async Task ActivateRejectsAProductThatIsAlreadyActive()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var created = await ReadProductAsync(
            await CreateProductAsync(client, TenantId, "Vela de soja", "VS-001"));

        var response = await ActivateAsync(client, TenantId, created.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("catalog.product.already_active", body, StringComparison.Ordinal);
    }

    // CA-CAT-07-03 — el criterio que justifica el slice. Sin esto se puede entregar un endpoint
    // que responde 200 y deja el producto igual de inservible.
    [Fact]
    public async Task ActivateReopensEditingEndToEnd()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var created = await ReadProductAsync(
            await CreateProductAsync(client, TenantId, "Vela de soja", "VS-001"));
        await DeactivateAsync(client, TenantId, created.Id);

        // Antes de activar, editar está cerrado: es el callejón sin salida que el slice abre.
        var blocked = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{created.Id}",
            new { name = "Vela de coco", code = "VS-001", pricing = new { baseUsd = 10m, finalUsd = 10m } },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);

        await ActivateAsync(client, TenantId, created.Id);

        var edited = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{created.Id}",
            new { name = "Vela de coco", code = "VS-001", pricing = new { baseUsd = 10m, finalUsd = 10m } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal("Vela de coco", (await ReadProductAsync(edited)).Name);
    }

    // CA-CAT-07-04: 404, nunca 403 ni 200. Y el producto ajeno tiene que seguir inactivo — un
    // handler que active primero y filtre después devolvería 404 con el efecto ya hecho.
    [Fact]
    public async Task ActivateDoesNotReachAcrossTenants()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var owner = CreateClient(factory, OtherSubjectId, OtherTenantId, ManagePermissions);
        using var intruder = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var foreign = await ReadProductAsync(
            await CreateProductAsync(owner, OtherTenantId, "Vela ajena", "VA-001"));
        await DeactivateAsync(owner, OtherTenantId, foreign.Id);

        var response = await ActivateAsync(intruder, TenantId, foreign.Id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.False(await QueryIsActiveAsync(connection, foreign.Id));
    }

    // CA-CAT-07-05
    [Fact]
    public async Task ActivateReturnsNotFoundForAnUnknownId()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var response = await ActivateAsync(client, TenantId, Guid.CreateVersion7());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // CA-CAT-07-06: sin el permiso, 403 y el producto sigue inactivo. La autorización va antes
    // de tocar el repositorio; la revisión de CAT-02 ya corrigió ese orden una vez.
    [Fact]
    public async Task ActivateRequiresTheManagePermission()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var manager = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        using var reader = CreateClient(factory, SubjectId, TenantId, ReadOnlyPermissions);

        var created = await ReadProductAsync(
            await CreateProductAsync(manager, TenantId, "Vela de soja", "VS-001"));
        await DeactivateAsync(manager, TenantId, created.Id);

        var response = await ActivateAsync(reader, TenantId, created.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.False(await QueryIsActiveAsync(connection, created.Id));
    }

    // CA-CAT-07-07: la auditoría se prueba por lo que escribe **y** por lo que no. El 422 y la
    // transacción abortan juntos, así que el rechazo no puede dejar rastro en el outbox.
    [Fact]
    public async Task ActivateRecordsOneAuditEventAndTheRejectionRecordsNone()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var created = await ReadProductAsync(
            await CreateProductAsync(client, TenantId, "Vela de soja", "VS-001"));
        await DeactivateAsync(client, TenantId, created.Id);

        await ActivateAsync(client, TenantId, created.Id);
        var rejected = await ActivateAsync(client, TenantId, created.Id);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Single(await QueryAuditEventsAsync(connection, "catalog.product.activated"));
    }

    // CA-CAT-07-08: Version es el token de concurrencia optimista. Sin el incremento, dos
    // escrituras que se solapan se pisan en silencio y ninguna aserción sobre is_active lo nota.
    // Create deja 1, Deactivate 2, Activate 3.
    [Fact]
    public async Task ActivateAdvancesVersionAndUpdatedAt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var created = await ReadProductAsync(
            await CreateProductAsync(client, TenantId, "Vela de soja", "VS-001"));
        await DeactivateAsync(client, TenantId, created.Id);

        await ActivateAsync(client, TenantId, created.Id);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3L, await QueryVersionAsync(connection, created.Id));
        Assert.True(await QueryUpdatedAfterCreatedAsync(connection, created.Id));
    }

    // CA-CAT-07-09: ancla la afirmación sobre el índice. IX_products_tenant_code es único **sin
    // filtro parcial**, así que desactivar nunca liberó el código y reactivar no puede colisionar.
    // Si algún día alguien le agrega un filtro parcial, esta prueba se cae y avisa.
    [Fact]
    public async Task AnInactiveProductNeverReleasedItsCode()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var created = await ReadProductAsync(
            await CreateProductAsync(client, TenantId, "Vela de soja", "VS-001"));
        await DeactivateAsync(client, TenantId, created.Id);

        var duplicate = await CreateProductAsync(client, TenantId, "Otra vela", "VS-001");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
        var body = await duplicate.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("catalog.product.code_taken", body, StringComparison.Ordinal);

        var response = await ActivateAsync(client, TenantId, created.Id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task<HttpResponseMessage> CreateProductAsync(
        HttpClient client,
        string tenantId,
        string name,
        string code) =>
        client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products",
            new { name, code, pricing = new { baseUsd = 10m, finalUsd = 10m } },
            TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> DeactivateAsync(
        HttpClient client,
        string tenantId,
        Guid productId) =>
        client.PostAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products/{productId}/deactivate",
            content: null,
            TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> ActivateAsync(
        HttpClient client,
        string tenantId,
        Guid productId) =>
        client.PostAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products/{productId}/activate",
            content: null,
            TestContext.Current.CancellationToken);

    private static async Task<ProductResponse> ReadProductAsync(HttpResponseMessage response)
    {
        var product = await response.Content.ReadFromJsonAsync<ProductResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(product);
        return product;
    }

    private static async Task<bool> QueryIsActiveAsync(
        NpgsqlConnection connection,
        Guid productId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT is_active FROM catalog.products WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", productId);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(value);
        return (bool)value;
    }

    // Product.Version es long y la columna es bigint. El cast a int compila y revienta en
    // runtime con InvalidCastException, que es cómo se descubrió.
    private static async Task<long> QueryVersionAsync(
        NpgsqlConnection connection,
        Guid productId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT version FROM catalog.products WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", productId);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(value);
        return (long)value;
    }

    private static async Task<bool> QueryUpdatedAfterCreatedAsync(
        NpgsqlConnection connection,
        Guid productId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT updated_at > created_at FROM catalog.products WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", productId);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(value);
        return (bool)value;
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
            // Fijado, nunca heredado de appsettings.json. SDD-CT-17.
            builder.UseSetting("Notifications:EmailProvider", "log");
        }
    }
}
