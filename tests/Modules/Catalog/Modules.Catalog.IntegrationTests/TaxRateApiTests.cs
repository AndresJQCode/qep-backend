using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Catalog.IntegrationTests;

public sealed class TaxRateApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-000000000001";
    private const string SubjectId = "01900000-0000-7000-8000-000000000002";
    private const string OtherTenantId = "01900000-0000-7000-8000-0000000000ff";
    private const string OtherSubjectId = "01900000-0000-7000-8000-0000000000fe";

    private static readonly string[] ManagePermissions =
        [CatalogPermissions.TaxRateRead, CatalogPermissions.TaxRateManage];

    // CA-CAT-03-01, primera mitad: la ruta responde para el tenant autenticado.
    [Fact]
    public async Task ListReturnsAnEmptyCatalogForANewTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(
            factory, SubjectId, TenantId, CatalogPermissions.TaxRateRead);

        var body = await ListAsync(client, TenantId);

        Assert.Empty(body);
    }

    // CA-CAT-03-01, la mitad que importa: sin nada sembrado, una lista vacía no prueba nada
    // sobre el aislamiento entre tenants.
    [Fact]
    public async Task ListReturnsOnlyTheTaxRatesOfTheAuthenticatedTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var owner = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        using var other = CreateClient(
            factory, OtherSubjectId, OtherTenantId, ManagePermissions);

        await CreateTaxRateAsync(owner, TenantId, "IVA general", 19);
        await CreateTaxRateAsync(other, OtherTenantId, "IVA ajeno", 5);

        var body = await ListAsync(owner, TenantId);

        var single = Assert.Single(body);
        Assert.Equal("IVA general", single.Name);
        Assert.Equal(19, single.Percentage);
    }

    // CA-CAT-03-02: el handler revalida el tenant activo contra el de la ruta antes de tocar el
    // repositorio, así que esto es 403 y no 404 — un 404 confirmaría que el id existe en otro
    // tenant. El llamador TIENE el permiso a propósito: si no, el 403 vendría del permiso
    // faltante y la prueba sobreviviría a que se rompa el aislamiento.
    [Fact]
    public async Task GetForAnotherTenantIsForbiddenAndNotNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var owner = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        using var intruder = CreateClient(
            factory, OtherSubjectId, OtherTenantId, ManagePermissions);

        var created = await ReadTaxRateAsync(
            await CreateTaxRateAsync(owner, TenantId, "IVA general", 19));

        var response = await intruder.GetAsync(
            $"/api/v1/tenants/{TenantId}/catalog/tax-rates/{created.Id}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // CA-CAT-03-03: leer no es gestionar, y el rechazo no deja rastro.
    [Fact]
    public async Task CreateWithOnlyTheReadPermissionIsForbiddenAndPersistsNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var reader = CreateClient(
            factory, SubjectId, TenantId, CatalogPermissions.TaxRateRead);

        var response = await CreateTaxRateAsync(reader, TenantId, "IVA general", 19);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await ListAsync(reader, TenantId));
    }

    // CA-CAT-03-04
    [Fact]
    public async Task CreateReturnsCreatedAndTheTaxRateIsReadableAfterwards()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var response = await CreateTaxRateAsync(client, TenantId, "IVA general", 19);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadTaxRateAsync(response);
        Assert.True(created.IsActive);
        Assert.Equal("IVA general", created.Name);
        Assert.Equal(19, created.Percentage);
        Assert.Equal(created.CreatedAt, created.UpdatedAt);

        var fetched = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/catalog/tax-rates/{created.Id}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
    }

    // CA-CAT-03-04, la mitad que el status HTTP no da: la auditoría tiene que estar en el outbox,
    // commiteada con la tasa. Se asserta sobre platform.outbox_messages y no sobre audit.entries
    // a propósito: catalog usa el camino de outbox, así que audit.entries recién aparece cuando
    // corre el worker de proyección, lo que volvería esta aserción una carrera.
    [Fact]
    public async Task CreateWritesExactlyOneAuditEventToTheOutbox()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var created = await ReadTaxRateAsync(
            await CreateTaxRateAsync(client, TenantId, "IVA general", 19));

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var events = await QueryAuditEventsAsync(connection, "catalog.tax_rate.created");
        var single = Assert.Single(events);
        Assert.Contains(TenantId, single, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(SubjectId, single, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(created.Id.ToString(), single, StringComparison.OrdinalIgnoreCase);
    }

    // CA-CAT-03-05: el mapa de campos viene del validador de FluentValidation, no de la
    // excepción de dominio, que sólo lleva un código.
    [Fact]
    public async Task CreateWithABlankNameReturnsUnprocessableWithTheFieldMap()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var response = await CreateTaxRateAsync(client, TenantId, "   ", 19);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("validation.failed", body, StringComparison.Ordinal);
        Assert.Contains("errors", body, StringComparison.Ordinal);
        Assert.Contains("Name", body, StringComparison.OrdinalIgnoreCase);
    }

    // CA-CAT-03-06: los dos extremos fuera de rango, no uno. Un guard escrito sólo contra
    // negativos deja pasar el 101, y el porcentaje llega a base sin que nadie se entere.
    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task CreateWithAPercentageOutOfRangeIsUnprocessable(int percentage)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var response = await CreateTaxRateAsync(client, TenantId, "IVA raro", percentage);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("Percentage", body, StringComparison.OrdinalIgnoreCase);
    }

    // CA-CAT-03-06, el lado positivo: 0 es el exento colombiano y 100 el límite superior. Sin
    // esto, un guard con > y < en vez de >= y <= pasa desapercibido hasta que alguien carga una
    // tasa exenta y le rebota.
    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task CreateAcceptsTheBoundaryPercentages(int percentage)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var response = await CreateTaxRateAsync(client, TenantId, $"Tasa {percentage}", percentage);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(percentage, (await ReadTaxRateAsync(response)).Percentage);
    }

    // CA-CAT-03-07
    [Fact]
    public async Task GetUpdateAndDeactivateReturnNotFoundForAnUnknownId()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        var missing = Guid.CreateVersion7();

        var get = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/catalog/tax-rates/{missing}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);

        var update = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/tax-rates/{missing}",
            new { name = "IVA general", percentage = 19 },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);

        var deactivate = await client.PostAsync(
            $"/api/v1/tenants/{TenantId}/catalog/tax-rates/{missing}/deactivate",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, deactivate.StatusCode);
    }

    // CA-CAT-03-08 y CA-CAT-03-09: inactivar dos veces es un error de negocio, no un éxito
    // silencioso, y no tiene que llegar a la base como un 500.
    [Fact]
    public async Task DeactivateTurnsTheTaxRateInactiveAndRejectsASecondAttempt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var created = await ReadTaxRateAsync(
            await CreateTaxRateAsync(client, TenantId, "IVA general", 19));
        var url = $"/api/v1/tenants/{TenantId}/catalog/tax-rates/{created.Id}/deactivate";

        var first = await client.PostAsync(
            url, content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.False((await ReadTaxRateAsync(first)).IsActive);

        var second = await client.PostAsync(
            url, content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("catalog.tax_rate.already_inactive", body, StringComparison.Ordinal);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Single(await QueryAuditEventsAsync(connection, "catalog.tax_rate.deactivated"));
    }

    // Actualizar cambia los campos, avanza updatedAt y deja su propia entrada de auditoría.
    [Fact]
    public async Task UpdateChangesTheFieldsAndAdvancesUpdatedAt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var created = await ReadTaxRateAsync(
            await CreateTaxRateAsync(client, TenantId, "IVA general", 19));

        var response = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/tax-rates/{created.Id}",
            new { name = "IVA reducido", percentage = 5 },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ReadTaxRateAsync(response);
        Assert.Equal("IVA reducido", updated.Name);
        Assert.Equal(5, updated.Percentage);
        Assert.True(updated.UpdatedAt >= created.UpdatedAt);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Single(await QueryAuditEventsAsync(connection, "catalog.tax_rate.updated"));
    }

    /// <summary>
    /// CA-CAT-03-10. Dos mitades, y la segunda es la que importa.
    ///
    /// Que el permiso figure en /authorization/catalog prueba que existe su PermissionDefinition.
    /// Que una llamada autorizada devuelva 200 y no 500 prueba que existe su AddPolicy — la otra
    /// mitad, que se registra a mano y por separado. Sin ella RequireAuthorization no resuelve la
    /// política y el síntoma es 500, que no se parece en nada a su causa.
    /// </summary>
    [Fact]
    public async Task TaxRatePermissionsArePublishedAndTheirPoliciesResolve()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        // El catálogo de autorización está protegido por tenancy.membership.read: tener los
        // permisos de catalog no alcanza para leer el catálogo que los publica.
        using var client = CreateClient(
            factory,
            SubjectId,
            TenantId,
            [.. ManagePermissions, "tenancy.membership.read"]);

        var catalog = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/authorization/catalog",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        var body = await catalog.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains(CatalogPermissions.TaxRateRead, body, StringComparison.Ordinal);
        Assert.Contains(CatalogPermissions.TaxRateManage, body, StringComparison.Ordinal);

        // La política de lectura resuelve.
        var list = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/catalog/tax-rates",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        // Y la de gestión también: es la que RequireAuthorization no resolvería si faltara.
        var created = await CreateTaxRateAsync(client, TenantId, "IVA general", 19);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    // CA-CAT-03-11: la violación de IX_tax_rates_tenant_name sale como 422 con su código de
    // dominio. Sin la traducción por nombre de índice es un 500 — la forma de SDD-CT-06.
    [Fact]
    public async Task CreatingTheSameNameTwiceInATenantReturnsNameTaken()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var first = await CreateTaxRateAsync(client, TenantId, "IVA general", 19);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await CreateTaxRateAsync(client, TenantId, "IVA general", 5);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("catalog.tax_rate.name_taken", body, StringComparison.Ordinal);
    }

    // CA-CAT-03-11, segunda mitad: la unicidad es por tenant, no global.
    [Fact]
    public async Task TheSameNameIsAcceptedInADifferentTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var owner = CreateClient(factory, SubjectId, TenantId, ManagePermissions);
        using var other = CreateClient(
            factory, OtherSubjectId, OtherTenantId, ManagePermissions);

        var first = await CreateTaxRateAsync(owner, TenantId, "IVA general", 19);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await CreateTaxRateAsync(other, OtherTenantId, "IVA general", 19);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
    }

    /// <summary>
    /// El defecto que la revisión de 4 lentes de CAT-02 encontró en Product, verificado acá
    /// antes de que pueda existir: los dos índices únicos del esquema catalog tienen que
    /// devolver códigos distintos. Colapsarlos en una sola rama manda al llamador a corregir el
    /// campo equivocado, que es exactamente SDD-CT-06.
    /// </summary>
    [Fact]
    public async Task TheTwoUniqueIndexesOfTheSchemaReturnTheirOwnDomainCode()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(
            factory,
            SubjectId,
            TenantId,
            [.. ManagePermissions, CatalogPermissions.ProductManage]);

        await CreateTaxRateAsync(client, TenantId, "IVA general", 19);
        var taxRateClash = await CreateTaxRateAsync(client, TenantId, "IVA general", 5);
        var taxRateBody = await taxRateClash.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            new { name = "Vela de soja", code = "VS-001" },
            TestContext.Current.CancellationToken);
        var productClash = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            new { name = "Otra vela", code = "VS-001" },
            TestContext.Current.CancellationToken);
        var productBody = await productClash.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Contains("catalog.tax_rate.name_taken", taxRateBody, StringComparison.Ordinal);
        Assert.DoesNotContain("catalog.product.code_taken", taxRateBody, StringComparison.Ordinal);
        Assert.Contains("catalog.product.code_taken", productBody, StringComparison.Ordinal);
        Assert.DoesNotContain("catalog.tax_rate.name_taken", productBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// Hallazgo `A` de la revisión de CAT-03: `Version` existía y **nada lo ejercitaba**.
    ///
    /// La prueba unitaria `UpdateAdvancesTheConcurrencyToken` sólo demuestra que un contador en
    /// memoria incrementa. No demuestra que `IsConcurrencyToken()` esté mapeado, que el `UPDATE`
    /// lleve la versión en su `WHERE`, ni que `DbUpdateConcurrencyException` se traduzca a
    /// `RequestConcurrencyException`. Sin esta prueba, borrar el `.IsConcurrencyToken()` del
    /// `CatalogDbContext` dejaba las 36 pruebas en verde.
    ///
    /// El escenario es el mismo con el que se cerró el hallazgo equivalente en `Product`: una
    /// edición que leyó la tasa activa y una inactivación que commitea en el medio.
    /// `EnsureActive()` ya pasó contra la copia en memoria del editor, y como esa edición no toca
    /// `IsActive`, EF no la incluye en el `SET`: sin token, el `UPDATE` entra sin condición sobre
    /// el estado real y queda una tasa **editada después de inactivarse**.
    ///
    /// El competidor va por la API y no por SQL a propósito: pasa por el dominio, que es quien
    /// incrementa la versión, y así la prueba no nombra la columna. Y va intercalado en vez de en
    /// paralelo porque una carrera de dos requests no falla de forma reproducible.
    /// </summary>
    [Fact]
    public async Task EditingATaxRateDeactivatedMidFlightIsRefusedInsteadOfOverwritingIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var created = await ReadTaxRateAsync(
            await CreateTaxRateAsync(client, TenantId, "IVA general", 19));

        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ITaxRateRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<ICatalogUnitOfWork>();

        // El que va a perder lee primero: se lleva la tasa activa.
        var stale = await repository.FindAsync(
            Guid.Parse(TenantId),
            new TaxRateId(created.Id),
            TestContext.Current.CancellationToken);
        Assert.NotNull(stale);
        Assert.True(stale!.IsActive);

        // Otro request inactiva y commitea, en su propia unidad de trabajo.
        var deactivate = await client.PostAsync(
            $"/api/v1/tenants/{TenantId}/catalog/tax-rates/{created.Id}/deactivate",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        // Recién ahora escribe el primero, sobre una copia que ya no refleja la base.
        stale.Update("IVA reducido", 5, DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<RequestConcurrencyException>(
            () => unitOfWork.SaveChangesAsync(TestContext.Current.CancellationToken));

        // Y la inactivación sigue en pie: la edición perdida no dejó rastro.
        var current = await ReadTaxRateAsync(
            await client.GetAsync(
                $"/api/v1/tenants/{TenantId}/catalog/tax-rates/{created.Id}",
                TestContext.Current.CancellationToken));
        Assert.False(current.IsActive);
        Assert.Equal("IVA general", current.Name);
        Assert.Equal(19, current.Percentage);
    }

    private static Task<HttpResponseMessage> CreateTaxRateAsync(
        HttpClient client,
        string tenantId,
        string name,
        int percentage) =>
        client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/tax-rates",
            new { name, percentage },
            TestContext.Current.CancellationToken);

    private static async Task<TaxRateResponse> ReadTaxRateAsync(HttpResponseMessage response)
    {
        var taxRate = await response.Content.ReadFromJsonAsync<TaxRateResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(taxRate);
        return taxRate;
    }

    private static async Task<IReadOnlyCollection<TaxRateResponse>> ListAsync(
        HttpClient client,
        string tenantId)
    {
        var response = await client.GetAsync(
            $"/api/v1/tenants/{tenantId}/catalog/tax-rates",
            TestContext.Current.CancellationToken);

        // El status se assertea acá y no en el llamador a propósito. Sin esto, un 403 o un 500
        // deserializan igual a TaxRatesResponse —con Items en null— y el fallo sale como
        // ArgumentNullException sobre 'collection', que no dice absolutamente nada de la causa.
        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<TaxRatesResponse>(
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

    // El stub de desarrollo concede sólo los defaults de tenancy cuando X-Permissions no está,
    // así que un permiso de catalog hay que pedirlo explícitamente. Sin esto, una prueba
    // cross-tenant pasaría simplemente porque el llamador no tenía ningún permiso de catalog, y
    // seguiría pasando aunque se rompiera el aislamiento de tenant.
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
