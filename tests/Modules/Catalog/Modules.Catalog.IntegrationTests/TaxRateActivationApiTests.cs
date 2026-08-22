using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Catalog.Application;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// CAT-08 — reactivación de tasa de impuesto.
///
/// La asimetría que este archivo cierra es la misma que <c>CAT-07</c> cerró para <c>Product</c>,
/// pero acá tenía una vuelta de tuerca que la hacía peor. <c>CAT-06</c> agregó el <c>DELETE</c>,
/// y es <c>RESTRICT</c>: no borra una tasa que algún producto use. Cruzado con el
/// <c>EnsureActive()</c> de <c>Update</c>, una tasa inactiva **en uso** no se podía editar, ni
/// borrar, ni reactivar — sólo un <c>UPDATE</c> por SQL la sacaba de ahí. Eso es lo que
/// CA-CAT-08-10 reproduce entero antes de resolverlo.
/// </summary>
public sealed class TaxRateActivationApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-000000000041";
    private const string SubjectId = "01900000-0000-7000-8000-000000000042";
    private const string OtherTenantId = "01900000-0000-7000-8000-0000000000aa";
    private const string OtherSubjectId = "01900000-0000-7000-8000-0000000000ab";

    private static readonly string[] ManagePermissions =
    [
        CatalogPermissions.ProductRead,
        CatalogPermissions.ProductManage,
        CatalogPermissions.TaxRateRead,
        CatalogPermissions.TaxRateManage
    ];

    private static readonly string[] ReadOnlyPermissions =
    [
        CatalogPermissions.ProductRead,
        CatalogPermissions.TaxRateRead
    ];

    // CA-CAT-08-01
    [Fact]
    public async Task ActivateBringsAnInactiveTaxRateBackAndPersistsIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var id = await CreateTaxRateAsync(client, TenantId, "IVA general", 19);
        await DeactivateAsync(client, TenantId, id);

        var response = await ActivateAsync(client, TenantId, id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await ReadTaxRateAsync(response)).IsActive);

        // El 200 no alcanza: lo que importa es la fila. Un handler que devuelva el DTO sin
        // guardar pasaría la aserción de arriba y fallaría ésta.
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.True(await QueryIsActiveAsync(connection, id));
    }

    // CA-CAT-08-02: activar algo ya activo es un error de negocio. Ni 200 silencioso, ni 500.
    [Fact]
    public async Task ActivateRejectsATaxRateThatIsAlreadyActive()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var id = await CreateTaxRateAsync(client, TenantId, "IVA general", 19);

        var response = await ActivateAsync(client, TenantId, id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("catalog.tax_rate.already_active", body, StringComparison.Ordinal);
    }

    // CA-CAT-08-03: la versión mínima del callejón sin salida, sin producto de por medio.
    [Fact]
    public async Task ActivateReopensEditing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var id = await CreateTaxRateAsync(client, TenantId, "IVA general", 19);
        await DeactivateAsync(client, TenantId, id);

        var blocked = await UpdateAsync(client, TenantId, id, "IVA reducido", 5);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);

        await ActivateAsync(client, TenantId, id);

        var edited = await UpdateAsync(client, TenantId, id, "IVA reducido", 5);

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var body = await ReadTaxRateAsync(edited);
        Assert.Equal("IVA reducido", body.Name);
        Assert.Equal(5, body.Percentage);
    }

    // CA-CAT-08-04: 404, nunca 403 ni 200. Y la tasa ajena tiene que seguir inactiva — un handler
    // que active primero y filtre después devolvería 404 con el efecto ya hecho.
    [Fact]
    public async Task ActivateDoesNotReachAcrossTenants()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var owner = CreateClient(factory, OtherSubjectId, OtherTenantId, ManagePermissions);
        using var intruder = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var foreign = await CreateTaxRateAsync(owner, OtherTenantId, "IVA ajeno", 19);
        await DeactivateAsync(owner, OtherTenantId, foreign);

        var response = await ActivateAsync(intruder, TenantId, foreign);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.False(await QueryIsActiveAsync(connection, foreign));
    }

    // CA-CAT-08-05
    [Fact]
    public async Task ActivateReturnsNotFoundForAnUnknownId()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var response = await ActivateAsync(client, TenantId, Guid.CreateVersion7());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // CA-CAT-08-06: sin el permiso, 403 y la tasa sigue inactiva. La autorización va antes de
    // tocar el repositorio.
    [Fact]
    public async Task ActivateRequiresTheManagePermission()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var manager = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        using var reader = CreateClient(factory, SubjectId, TenantId, ReadOnlyPermissions);

        var id = await CreateTaxRateAsync(manager, TenantId, "IVA general", 19);
        await DeactivateAsync(manager, TenantId, id);

        var response = await ActivateAsync(reader, TenantId, id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.False(await QueryIsActiveAsync(connection, id));

        // El 403 tampoco deja rastro en el outbox, y esto hay que afirmarlo acá y no sólo en el
        // runtime: si alguien moviera el auditPublisher.Publish por encima de
        // CatalogAuthorization.EnsureAuthorized, la fila de auditoría aparecería sin que el estado
        // cambiara, y una aserción que sólo mire is_active seguiría en verde.
        Assert.Empty(await QueryAuditEventsAsync(connection, "catalog.tax_rate.activated", id));
    }

    // CA-CAT-08-07: la auditoría se prueba por lo que escribe **y** por lo que no. El 422 y la
    // transacción abortan juntos, así que el rechazo no puede dejar rastro en el outbox.
    [Fact]
    public async Task ActivateRecordsOneAuditEventAndTheRejectionRecordsNone()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var id = await CreateTaxRateAsync(client, TenantId, "IVA general", 19);
        await DeactivateAsync(client, TenantId, id);

        await ActivateAsync(client, TenantId, id);
        var rejected = await ActivateAsync(client, TenantId, id);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Single(await QueryAuditEventsAsync(connection, "catalog.tax_rate.activated", id));
    }

    // CA-CAT-08-08: Version es el token de concurrencia optimista. Sin el incremento, dos
    // escrituras que se solapan se pisan en silencio y ninguna aserción sobre is_active lo nota.
    // Create deja 1, Deactivate 2, Activate 3.
    [Fact]
    public async Task ActivateAdvancesVersionAndUpdatedAt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var id = await CreateTaxRateAsync(client, TenantId, "IVA general", 19);
        await DeactivateAsync(client, TenantId, id);

        await ActivateAsync(client, TenantId, id);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3L, await QueryVersionAsync(connection, id));
        Assert.True(await QueryUpdatedAfterCreatedAsync(connection, id));
    }

    // CA-CAT-08-09: ancla la afirmación sobre el índice. IX_tax_rates_tenant_name es único **sin
    // filtro parcial**, así que desactivar nunca liberó el nombre y reactivar no puede colisionar.
    // Si algún día alguien le agrega un filtro parcial, esta prueba se cae y avisa.
    [Fact]
    public async Task AnInactiveTaxRateNeverReleasedItsName()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var id = await CreateTaxRateAsync(client, TenantId, "IVA general", 19);
        await DeactivateAsync(client, TenantId, id);

        var duplicate = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/tax-rates",
            new { name = "IVA general", percentage = 5 },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
        var body = await duplicate.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("catalog.tax_rate.name_taken", body, StringComparison.Ordinal);

        var response = await ActivateAsync(client, TenantId, id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // CA-CAT-08-10 — el criterio que justifica el slice. Reproduce el atolladero completo antes
    // de resolverlo: una tasa inactiva que un producto usa no se puede editar (EnsureActive) ni
    // borrar (la FK es RESTRICT). Sin este endpoint, la única salida era un UPDATE por SQL.
    [Fact]
    public async Task ActivateRescuesAnInactiveTaxRateThatCannotBeDeletedBecauseAProductUsesIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var id = await CreateTaxRateAsync(client, TenantId, "IVA general", 19);
        var product = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            new { name = "Vela de soja", code = "VS-101", taxRateId = id, pricing = new { baseUsd = 10m, finalUsd = 10m } },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, product.StatusCode);

        await DeactivateAsync(client, TenantId, id);

        // Las dos puertas cerradas, antes de abrir la tercera.
        var blockedUpdate = await UpdateAsync(client, TenantId, id, "IVA corregido", 21);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blockedUpdate.StatusCode);
        Assert.Contains(
            "catalog.tax_rate.inactive",
            await blockedUpdate.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);

        var blockedDelete = await client.DeleteAsync(
            $"/api/v1/tenants/{TenantId}/catalog/tax-rates/{id}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blockedDelete.StatusCode);
        Assert.Contains(
            "catalog.tax_rate.in_use",
            await blockedDelete.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);

        // La salida que este slice agrega.
        var rescued = await ActivateAsync(client, TenantId, id);
        Assert.Equal(HttpStatusCode.OK, rescued.StatusCode);

        var edited = await UpdateAsync(client, TenantId, id, "IVA corregido", 21);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal(21, (await ReadTaxRateAsync(edited)).Percentage);
    }

    private static async Task<Guid> CreateTaxRateAsync(
        HttpClient client,
        string tenantId,
        string name,
        int percentage)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/tax-rates",
            new { name, percentage },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadTaxRateAsync(response)).Id;
    }

    private static Task<HttpResponseMessage> DeactivateAsync(
        HttpClient client,
        string tenantId,
        Guid taxRateId) =>
        client.PostAsync(
            $"/api/v1/tenants/{tenantId}/catalog/tax-rates/{taxRateId}/deactivate",
            content: null,
            TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> ActivateAsync(
        HttpClient client,
        string tenantId,
        Guid taxRateId) =>
        client.PostAsync(
            $"/api/v1/tenants/{tenantId}/catalog/tax-rates/{taxRateId}/activate",
            content: null,
            TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> UpdateAsync(
        HttpClient client,
        string tenantId,
        Guid taxRateId,
        string name,
        int percentage) =>
        client.PutAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/tax-rates/{taxRateId}",
            new { name, percentage },
            TestContext.Current.CancellationToken);

    private static async Task<TaxRateResponse> ReadTaxRateAsync(HttpResponseMessage response)
    {
        var taxRate = await response.Content.ReadFromJsonAsync<TaxRateResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(taxRate);
        return taxRate;
    }

    private static async Task<bool> QueryIsActiveAsync(
        NpgsqlConnection connection,
        Guid taxRateId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT is_active FROM catalog.tax_rates WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", taxRateId);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(value);
        return (bool)value;
    }

    // Version es long y la columna es bigint: el cast a int compila y revienta en runtime.
    private static async Task<long> QueryVersionAsync(
        NpgsqlConnection connection,
        Guid taxRateId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT version FROM catalog.tax_rates WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", taxRateId);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(value);
        return (long)value;
    }

    private static async Task<bool> QueryUpdatedAfterCreatedAsync(
        NpgsqlConnection connection,
        Guid taxRateId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT updated_at > created_at FROM catalog.tax_rates WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", taxRateId);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(value);
        return (bool)value;
    }

    // El campo del payload de auditoría es resourceId, no entityId: filtrar por un campo
    // inexistente devuelve cero, que es indistinguible de que la auditoría no se escribió.
    private static async Task<IReadOnlyList<string>> QueryAuditEventsAsync(
        NpgsqlConnection connection,
        string action,
        Guid resourceId)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT payload::text
            FROM platform.outbox_messages
            WHERE event_name = 'platform.audit.recorded.v1'
              AND payload->>'action' = @action
              AND payload->>'resourceId' = @resourceId
            ORDER BY occurred_at
            """,
            connection);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("resourceId", resourceId.ToString());

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
