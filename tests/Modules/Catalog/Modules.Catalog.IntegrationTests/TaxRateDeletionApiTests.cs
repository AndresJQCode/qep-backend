using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Catalog.Application;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// CAT-06 — borrado de tasa de impuesto.
///
/// La operación que este archivo verifica no es «borrar»: es «borrar **si nadie la usa**». La FK
/// `FK_products_tax_rates_tax_rate_id` es `RESTRICT`, así que la base ya impone la condición; lo
/// que agrega el slice es que el llamador reciba un 422 que se entiende en vez de un 500.
/// </summary>
public sealed class TaxRateDeletionApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-000000000021";
    private const string SubjectId = "01900000-0000-7000-8000-000000000022";
    private const string OtherTenantId = "01900000-0000-7000-8000-0000000000dd";
    private const string OtherSubjectId = "01900000-0000-7000-8000-0000000000dc";

    private static readonly string[] All =
    [
        CatalogPermissions.ProductRead,
        CatalogPermissions.ProductManage,
        CatalogPermissions.TaxRateRead,
        CatalogPermissions.TaxRateManage
    ];

    private static readonly string[] ReadOnly =
    [
        CatalogPermissions.ProductRead,
        CatalogPermissions.TaxRateRead
    ];

    // CA-CAT-06-01
    [Fact]
    public async Task AnUnusedTaxRateIsDeletedAndDisappearsFromTheDatabase()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var taxRateId = await CreateTaxRateAsync(client, TenantId, "IVA que sobra", 19);

        var response = await DeleteTaxRateAsync(client, TenantId, taxRateId);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verificado en base, no por el listado: un filtro mal escrito lo escondería del GET
        // dejando la fila donde estaba.
        Assert.Equal(0, await CountTaxRateAsync(database, taxRateId));
    }

    /// <summary>
    /// CA-CAT-06-02 — el criterio que justifica el slice.
    ///
    /// Sin la comprobación, este caso llega a PostgreSQL, vuelve como violación de la FK
    /// `RESTRICT` y sale como **500 `server.unexpected`** — y, por el hallazgo `C` de `CAT-04`,
    /// con el nombre de la constraint adentro del mensaje.
    /// </summary>
    [Fact]
    public async Task ATaxRateInUseByAProductIsRejectedAndSurvives()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var taxRateId = await CreateTaxRateAsync(client, TenantId, "IVA en uso", 19);
        var created = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            new { name = "Vela de soja", code = "VS-001", taxRateId },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var response = await DeleteTaxRateAsync(client, TenantId, taxRateId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(
            "catalog.tax_rate.in_use",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);

        Assert.Equal(1, await CountTaxRateAsync(database, taxRateId));
    }

    /// <summary>
    /// CA-CAT-06-03 — aislamiento entre tenants.
    ///
    /// Un `DELETE` que devuelva 404 **y borre igual** sería la peor forma de esta fuga: la
    /// respuesta no deja rastro de lo que hizo. Por eso la aserción que importa no es el status
    /// sino el conteo en base.
    /// </summary>
    [Fact]
    public async Task ATaxRateFromAnotherTenantIsNotFoundAndIsNotDeleted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var owner = CreateClient(factory, SubjectId, TenantId, All);
        using var other = CreateClient(factory, OtherSubjectId, OtherTenantId, All);

        var foreignTaxRateId = await CreateTaxRateAsync(other, OtherTenantId, "IVA ajeno", 19);

        var response = await DeleteTaxRateAsync(owner, TenantId, foreignTaxRateId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, await CountTaxRateAsync(database, foreignTaxRateId));
    }

    // CA-CAT-06-04
    [Fact]
    public async Task AnUnknownTaxRateIsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var response = await DeleteTaxRateAsync(client, TenantId, Guid.CreateVersion7());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // CA-CAT-06-05: borrar es administrar. Leer no alcanza.
    [Fact]
    public async Task ReadPermissionAloneCannotDelete()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var manager = CreateClient(factory, SubjectId, TenantId, All);
        using var reader = CreateClient(factory, SubjectId, TenantId, ReadOnly);

        var taxRateId = await CreateTaxRateAsync(manager, TenantId, "IVA general", 19);

        var response = await DeleteTaxRateAsync(reader, TenantId, taxRateId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, await CountTaxRateAsync(database, taxRateId));
    }

    // CA-CAT-06-06: la auditoría y el borrado, en la misma transacción.
    [Fact]
    public async Task DeletingWritesExactlyOneAuditEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var taxRateId = await CreateTaxRateAsync(client, TenantId, "IVA que sobra", 19);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await DeleteTaxRateAsync(client, TenantId, taxRateId)).StatusCode);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Single(await QueryAuditEventsAsync(connection, "catalog.tax_rate.deleted"));
    }

    // CA-CAT-06-07: desactivar y borrar son operaciones distintas, y la segunda no depende de la
    // primera ni la excluye.
    [Fact]
    public async Task AnInactiveTaxRateThatNobodyUsesIsStillDeletable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var taxRateId = await CreateTaxRateAsync(client, TenantId, "IVA viejo", 16);
        var deactivate = await client.PostAsync(
            $"/api/v1/tenants/{TenantId}/catalog/tax-rates/{taxRateId}/deactivate",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        var response = await DeleteTaxRateAsync(client, TenantId, taxRateId);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await CountTaxRateAsync(database, taxRateId));
    }

    /// <summary>
    /// CA-CAT-06-08 — la carrera, y el código correcto para ella.
    ///
    /// Entre la consulta de «¿la usa alguien?» y el `COMMIT`, otra transacción puede crear un
    /// producto que la use. La ventana es chica y existe. Acá se reproduce el efecto insertando
    /// el producto **por SQL** después de que el handler ya decidió que la tasa estaba libre —
    /// que es lo que hace el helper de abajo — y lo que se verifica es que la violación de FK
    /// salga como `catalog.tax_rate.in_use`.
    ///
    /// **La mitad que importa es el código, no el status.** La misma constraint se viola cuando
    /// un producto apunta a una tasa inexistente, y ese caso ya se traducía a
    /// `catalog.product.tax_rate_not_found`. Devolver ése acá mandaría a corregir la entidad
    /// equivocada: el problema no es que la tasa no exista, es que sí existe y está en uso.
    /// </summary>
    [Fact]
    public async Task AForeignKeyViolationOnDeleteSaysInUseAndNotTaxRateNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, All);

        var taxRateId = await CreateTaxRateAsync(client, TenantId, "IVA en carrera", 19);

        // El producto se inserta por SQL, saltándose la API: así la fila existe sin que el
        // handler la haya visto, que es la forma observable de la carrera.
        await InsertProductWithTaxRateAsync(database, taxRateId);

        var response = await DeleteTaxRateAsync(client, TenantId, taxRateId);

        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("catalog.tax_rate.in_use", body, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "catalog.product.tax_rate_not_found",
            body,
            StringComparison.Ordinal);
    }

    private static Task<HttpResponseMessage> DeleteTaxRateAsync(
        HttpClient client,
        string tenantId,
        Guid taxRateId) =>
        client.DeleteAsync(
            $"/api/v1/tenants/{tenantId}/catalog/tax-rates/{taxRateId}",
            TestContext.Current.CancellationToken);

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

        var body = await response.Content.ReadFromJsonAsync<TaxRateResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body.Id;
    }

    private static async Task<int> CountTaxRateAsync(
        PostgreSqlContainer database,
        Guid taxRateId)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM catalog.tax_rates WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", taxRateId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task InsertProductWithTaxRateAsync(
        PostgreSqlContainer database,
        Guid taxRateId)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO catalog.products
                (id, tenant_id, name, code, is_active, version, created_at, updated_at,
                 tax_rate_id)
            VALUES (@id, @tenantId, 'Producto en carrera', 'PC-001', true, 1, now(), now(), @taxRateId)
            """,
            connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("tenantId", Guid.Parse(TenantId));
        command.Parameters.AddWithValue("taxRateId", taxRateId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
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
