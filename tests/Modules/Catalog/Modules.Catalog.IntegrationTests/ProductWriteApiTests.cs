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

    // CA-CAT-02-04: el evento de auditoría tiene que estar en el outbox, commiteado con el producto.
    // Se asserta sobre platform.outbox_messages y no sobre audit.entries a propósito: catalog usa
    // el camino de outbox, así que audit.entries recién aparece cuando corre el worker de
    // proyección de Audit, lo que volvería esta aserción una carrera.
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

    // CA-CAT-02-05: el mapa de campos viene del validador de FluentValidation, no de la
    // excepción de dominio, que sólo lleva un código.
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

    // CA-CAT-02-03: leer no es gestionar.
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

    // CA-CAT-02-12: la violación de unicidad en IX_products_tenant_code tiene que salir como 422
    // con el código de dominio. Sin la traducción es un 500 — la forma de SDD-CT-06.
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

    // CA-CAT-02-12, segunda mitad: la unicidad es por tenant, no global.
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

    // CA-CAT-02-01, la mitad que CAT-02a no podía cubrir: sin nada sembrado, una lista vacía
    // no prueba nada sobre el aislamiento.
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
            new { name = "Vela", code = "VS-001", pricing = new { baseUsd = 10m, finalUsd = 10m } },
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
            new { name = "Vela de cera", code = "VC-002", pricing = new { baseUsd = 10m, finalUsd = 10m } },
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

    // CA-CAT-02-08 y CA-CAT-02-09: inactivar dos veces es un error de negocio, no un éxito
    // silencioso, y no tiene que llegar a la base como un 500.
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

    // CA-CAT-02-11: los permisos no son sólo constantes en el código, los publica el catálogo
    // de autorización que la UI lee para decidir qué renderizar.
    [Fact]
    public async Task CatalogPermissionsArePublishedInTheAuthorizationCatalog()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        // El endpoint del catálogo está protegido por tenancy.membership.read, cuya definición
        // dice "consultar membresías y catálogo de roles/permisos". Tener los permisos de catalog
        // no alcanza para leer el catálogo que los publica.
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

    /// <summary>
    /// CA-CAT-02-02 con cuerpo inválido, el camino que la revisión de riesgo de CAT-02
    /// encontró sin cubrir.
    ///
    /// Falta de permiso y tenant ajeno **no** son el mismo caso. A quien le falta el permiso
    /// lo frena la política del endpoint, antes de que el handler exista, así que el validador
    /// nunca corre. El cruce de tenants es distinto: la política pasa —el permiso está— y
    /// quien rechaza es la revalidación del handler. Ahí sí importa el orden, y validar antes
    /// de autorizar le devuelve a un tenant ajeno el mapa de errores por campo: la forma del
    /// contrato, a alguien que no puede usarlo contra ese tenant.
    /// </summary>
    [Fact]
    public async Task CreateForAnotherTenantIsForbiddenBeforeTheBodyIsValidated()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        // Autenticado en OtherTenant y con el permiso de gestión: la política del endpoint pasa.
        using var intruder = CreateClient(
            factory, OtherSubjectId, OtherTenantId, ManagePermissions);

        var response = await CreateProductAsync(intruder, TenantId, string.Empty, string.Empty);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain("validation.failed", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// El `_` de LIKE coincide con un carácter cualquiera. Como el término del usuario se
    /// interpola en el patrón, `?search=_` devolvía el catálogo entero: lo contrario de
    /// filtrar. Los comodines tienen que ser sólo los que pone el repositorio.
    /// </summary>
    [Fact]
    public async Task SearchTreatsLikeWildcardsAsLiteralCharacters()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        await CreateProductAsync(client, TenantId, "Vela de soja", "VS-001");
        await CreateProductAsync(client, TenantId, "Mecha de algodón", "MC-002");
        await CreateProductAsync(client, TenantId, "Kit_A de armado", "KT-003");

        // Sólo el que lleva un guion bajo literal en el nombre.
        var underscore = await ListAsync(client, TenantId, "_");
        Assert.Single(underscore);
        Assert.Equal("KT-003", underscore.Single().Code);

        // El % no coincide con nada: ningún producto lo tiene en su texto.
        var percent = await ListAsync(client, TenantId, "%");
        Assert.Empty(percent);
    }

    // TaxRatePermissionsAreNotPublishedBeforeTheirSliceExists vivía acá y se borró en CAT-03,
    // tal como su propio comentario anticipaba: afirmaba que catalog.tax_rate NO aparece en
    // /authorization/catalog, y CAT-03 trajo los dos permisos junto con sus endpoints. Su
    // reemplazo es CA-CAT-03-10 en TaxRateApiTests, que ahora afirma lo contrario y además
    // verifica que la política resuelva.

    /// <summary>
    /// Hallazgo A de la revisión de CAT-02, al que llegaron por separado los lentes de
    /// fiabilidad y de resiliencia.
    ///
    /// Una edición que lee el producto activo y una inactivación que commitea en el medio.
    /// `EnsureActive()` del agregado ya pasó contra la copia en memoria del editor, y como esa
    /// edición no toca `IsActive`, EF no la incluye en el `SET`: sin token de concurrencia el
    /// `UPDATE` entra sin condición sobre el estado real. Quedaba un producto **editado después
    /// de inactivarse** —justo lo que `EnsureActive()` existe para impedir—, con dos entradas
    /// de auditoría `success` y un estado final que no corresponde a ninguna de las dos.
    ///
    /// El competidor va por la API y no por SQL a propósito: pasa por el dominio, que es quien
    /// incrementa la versión, y así la prueba no nombra la columna. Y va intercalado en vez de
    /// en paralelo porque una carrera de dos requests no falla de forma reproducible.
    /// </summary>
    [Fact]
    public async Task EditingAProductDeactivatedMidFlightIsRefusedInsteadOfOverwritingIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId, ManagePermissions);

        var created = await ReadProductAsync(
            await CreateProductAsync(client, TenantId, "Vela de soja", "VS-001"));

        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IProductRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<ICatalogUnitOfWork>();

        // El que va a perder lee primero: se lleva el producto activo.
        var stale = await repository.FindAsync(
            Guid.Parse(TenantId),
            new ProductId(created.Id),
            TestContext.Current.CancellationToken);
        Assert.NotNull(stale);
        Assert.True(stale!.IsActive);

        // Otro request inactiva y commitea, en su propia unidad de trabajo.
        var deactivate = await client.PostAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products/{created.Id}/deactivate",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        // Recién ahora escribe el primero, sobre una copia que ya no refleja la base.
        stale.Update(
            "Vela de soja premium", "VS-002", ProductDetails.Empty,
            new ProductPricing { BaseUsd = 10m, FinalUsd = 10m }, DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<RequestConcurrencyException>(
            () => unitOfWork.SaveChangesAsync(TestContext.Current.CancellationToken));

        // Y la inactivación sigue en pie: la edición perdida no dejó rastro.
        var current = await ReadProductAsync(
            await client.GetAsync(
                $"/api/v1/tenants/{TenantId}/catalog/products/{created.Id}",
                TestContext.Current.CancellationToken));
        Assert.False(current.IsActive);
        Assert.Equal("Vela de soja", current.Name);
        Assert.Equal("VS-001", current.Code);
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
            // Fijado, nunca heredado de appsettings.json. SDD-CT-17.
            builder.UseSetting("Notifications:EmailProvider", "log");
        }
    }
}
