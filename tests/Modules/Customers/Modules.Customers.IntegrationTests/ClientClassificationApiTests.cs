using System.Net;
using System.Net.Http.Json;
using Modules.Customers.Application;
using Npgsql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>
/// El catalogo de clasificaciones de cliente (nombre + prefijo), mismo shape que TaxRate en
/// Catalog: catalogo de referencia chico, tenant-scoped, con nombre y prefijo unicos por tenant
/// y estado activo/inactivo reversible.
/// </summary>
public sealed class ClientClassificationApiTests
{
    private static string ClassificationsUrl(string tenantId = TenantId) =>
        $"/api/v1/tenants/{tenantId}/customers/classifications";

    // CustomersApiHarness.CreateManager sólo concede CustomerRead/CustomerManage: es el manager
    // de Customer, no el de este recurso. Un cliente de clasificaciones necesita sus propios
    // permisos, mismo criterio que ManagePermissions en TaxRateApiTests.
    private static HttpClient CreateClassificationManager(QepApiFactory factory) =>
        CreateClient(
            factory,
            SubjectId,
            TenantId,
            CustomersPermissions.ClassificationRead,
            CustomersPermissions.ClassificationManage);

    [Fact]
    public async Task ListReturnsAnEmptyCatalogForANewTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClassificationManager(factory);

        var body = await ListAsync(client, TenantId);

        Assert.Empty(body);
    }

    [Fact]
    public async Task ListReturnsOnlyTheClassificationsOfTheAuthenticatedTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var owner = CreateClassificationManager(factory);
        using var other = CreateClient(
            factory,
            OtherSubjectId,
            OtherTenantId,
            CustomersPermissions.ClassificationRead,
            CustomersPermissions.ClassificationManage);

        await CreateClassificationAsync(owner, TenantId, "Mayorista", "MAY");
        await CreateClassificationAsync(other, OtherTenantId, "Ajena", "AJE");

        var body = await ListAsync(owner, TenantId);

        var single = Assert.Single(body);
        Assert.Equal("Mayorista", single.Name);
        Assert.Equal("MAY", single.Prefix);
    }

    [Fact]
    public async Task CreateReturnsCreatedAndTheClassificationIsReadableAfterwards()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClassificationManager(factory);

        var response = await CreateClassificationAsync(client, TenantId, "Mayorista", "MAY");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadClassificationAsync(response);
        Assert.True(created.IsActive);
        Assert.Equal("Mayorista", created.Name);
        Assert.Equal("MAY", created.Prefix);
        Assert.Equal(created.CreatedAt, created.UpdatedAt);
        Assert.Equal(
            $"{ClassificationsUrl()}/{created.Id}",
            response.Headers.Location?.ToString());

        var fetched = await client.GetAsync(
            $"{ClassificationsUrl()}/{created.Id}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
    }

    // Leer no es gestionar, y el rechazo no deja rastro.
    [Fact]
    public async Task CreateWithOnlyTheReadPermissionIsForbiddenAndPersistsNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var reader = CreateClient(
            factory, SubjectId, TenantId, CustomersPermissions.ClassificationRead);

        var response = await CreateClassificationAsync(reader, TenantId, "Mayorista", "MAY");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await ListAsync(reader, TenantId));
    }

    [Fact]
    public async Task CreateWithBlankNameOrPrefixReturnsThePerFieldErrorMap()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClassificationManager(factory);

        var response = await CreateClassificationAsync(client, TenantId, "   ", "   ");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var fields = await ValidationFieldsAsync(response);
        Assert.Contains("Name", fields);
        Assert.Contains("Prefix", fields);
    }

    [Fact]
    public async Task CreatingTheSameNameTwiceInATenantReturnsNameTaken()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClassificationManager(factory);
        var first = await CreateClassificationAsync(client, TenantId, "Mayorista", "MAY");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await CreateClassificationAsync(client, TenantId, "Mayorista", "OTR");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("customers.classification.name_taken", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreatingTheSamePrefixTwiceInATenantReturnsPrefixTaken()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClassificationManager(factory);
        var first = await CreateClassificationAsync(client, TenantId, "Mayorista", "MAY");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await CreateClassificationAsync(client, TenantId, "Minorista", "MAY");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("customers.classification.prefix_taken", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameNameAndPrefixAreAcceptedInADifferentTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var owner = CreateClassificationManager(factory);
        using var other = CreateClient(
            factory,
            OtherSubjectId,
            OtherTenantId,
            CustomersPermissions.ClassificationRead,
            CustomersPermissions.ClassificationManage);

        var first = await CreateClassificationAsync(owner, TenantId, "Mayorista", "MAY");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await CreateClassificationAsync(other, OtherTenantId, "Mayorista", "MAY");

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
    }

    [Fact]
    public async Task GetUpdateDeactivateAndDeleteReturnNotFoundForAnUnknownId()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClassificationManager(factory);
        var missing = Guid.CreateVersion7();

        var get = await client.GetAsync(
            $"{ClassificationsUrl()}/{missing}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);

        var update = await client.PutAsJsonAsync(
            $"{ClassificationsUrl()}/{missing}",
            new { name = "Mayorista", prefix = "MAY" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);

        var deactivate = await client.PostAsync(
            $"{ClassificationsUrl()}/{missing}/deactivate",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, deactivate.StatusCode);

        var delete = await client.DeleteAsync(
            $"{ClassificationsUrl()}/{missing}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    // El handler revalida el tenant activo contra el de la ruta antes de tocar el repositorio,
    // asi que esto es 403 y no 404 — un 404 confirmaria que el id existe en otro tenant.
    [Fact]
    public async Task GetForAnotherTenantIsForbiddenAndNotNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var owner = CreateClassificationManager(factory);
        using var intruder = CreateClient(
            factory,
            OtherSubjectId,
            OtherTenantId,
            CustomersPermissions.ClassificationRead,
            CustomersPermissions.ClassificationManage);

        var created = await ReadClassificationAsync(
            await CreateClassificationAsync(owner, TenantId, "Mayorista", "MAY"));

        var response = await intruder.GetAsync(
            $"{ClassificationsUrl()}/{created.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UpdateChangesTheFieldsAndAdvancesUpdatedAt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClassificationManager(factory);
        var created = await ReadClassificationAsync(
            await CreateClassificationAsync(client, TenantId, "Mayorista", "MAY"));

        var response = await client.PutAsJsonAsync(
            $"{ClassificationsUrl()}/{created.Id}",
            new { name = "Minorista", prefix = "MIN" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await ReadClassificationAsync(response);
        Assert.Equal("Minorista", updated.Name);
        Assert.Equal("MIN", updated.Prefix);
        Assert.True(updated.UpdatedAt >= created.UpdatedAt);
    }

    [Fact]
    public async Task UpdateOverAnInactiveClassificationIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClassificationManager(factory);
        var created = await ReadClassificationAsync(
            await CreateClassificationAsync(client, TenantId, "Mayorista", "MAY"));
        (await client.PostAsync(
            $"{ClassificationsUrl()}/{created.Id}/deactivate",
            content: null,
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var response = await client.PutAsJsonAsync(
            $"{ClassificationsUrl()}/{created.Id}",
            new { name = "Minorista", prefix = "MIN" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("customers.classification.inactive", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeactivateTurnsTheClassificationInactiveAndRejectsASecondAttempt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClassificationManager(factory);
        var created = await ReadClassificationAsync(
            await CreateClassificationAsync(client, TenantId, "Mayorista", "MAY"));
        var url = $"{ClassificationsUrl()}/{created.Id}/deactivate";

        var first = await client.PostAsync(url, content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.False((await ReadClassificationAsync(first)).IsActive);

        var second = await client.PostAsync(url, content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(
            "customers.classification.already_inactive", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActivateRevivesAndRejectsAnAlreadyActiveClassification()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClassificationManager(factory);
        var created = await ReadClassificationAsync(
            await CreateClassificationAsync(client, TenantId, "Mayorista", "MAY"));

        var alreadyActive = await client.PostAsync(
            $"{ClassificationsUrl()}/{created.Id}/activate",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, alreadyActive.StatusCode);
        var alreadyActiveBody = await alreadyActive.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains(
            "customers.classification.already_active", alreadyActiveBody, StringComparison.Ordinal);

        (await client.PostAsync(
            $"{ClassificationsUrl()}/{created.Id}/deactivate",
            content: null,
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var activate = await client.PostAsync(
            $"{ClassificationsUrl()}/{created.Id}/activate",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
        Assert.True((await ReadClassificationAsync(activate)).IsActive);
    }

    // El caso que hace falta declarar: sin esto se puede entregar un Activate que responde bien
    // y deja la clasificacion igual de congelada, porque Update sigue abriendo con EnsureActive().
    [Fact]
    public async Task ActivateReopensEditing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClassificationManager(factory);
        var created = await ReadClassificationAsync(
            await CreateClassificationAsync(client, TenantId, "Mayorista", "MAY"));
        (await client.PostAsync(
            $"{ClassificationsUrl()}/{created.Id}/deactivate",
            content: null,
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsync(
            $"{ClassificationsUrl()}/{created.Id}/activate",
            content: null,
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var response = await client.PutAsJsonAsync(
            $"{ClassificationsUrl()}/{created.Id}",
            new { name = "Minorista", prefix = "MIN" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Minorista", (await ReadClassificationAsync(response)).Name);
    }

    [Fact]
    public async Task DeleteRemovesTheClassificationPhysicallyAndItDisappearsFromTheList()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClassificationManager(factory);
        var created = await ReadClassificationAsync(
            await CreateClassificationAsync(client, TenantId, "Mayorista", "MAY"));

        var response = await client.DeleteAsync(
            $"{ClassificationsUrl()}/{created.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await ListAsync(client, TenantId));
        var get = await client.GetAsync(
            $"{ClassificationsUrl()}/{created.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    // FindAsync filtra por tenant, asi que una clasificacion ajena sale como 404 y nunca llega
    // al DELETE: la fila del otro tenant sigue existiendo despues.
    [Fact]
    public async Task DeleteOfAnotherTenantIsNotFoundAndDoesNotDeleteIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var owner = CreateClassificationManager(factory);
        using var intruder = CreateClient(
            factory,
            OtherSubjectId,
            OtherTenantId,
            CustomersPermissions.ClassificationRead,
            CustomersPermissions.ClassificationManage);
        var created = await ReadClassificationAsync(
            await CreateClassificationAsync(owner, TenantId, "Mayorista", "MAY"));

        // El intruso pega con su **propio** tenant en la ruta — pasa la autorizacion, que
        // coincide tenant de ruta con tenant activo — y el repositorio, acotado a ese tenant, no
        // encuentra la fila: 404 limpio, sin fuga. Pegarle con el tenant ajeno en la ruta es el
        // escenario de GetForAnotherTenantIsForbiddenAndNotNotFound, que es 403 y no esto.
        var response = await intruder.DeleteAsync(
            $"{ClassificationsUrl(OtherTenantId)}/{created.Id}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var single = Assert.Single(await ListAsync(owner, TenantId));
        Assert.Equal(created.Id, single.Id);
    }

    /// <summary>
    /// Dos mitades, y la segunda es la que importa: que el permiso figure en
    /// /authorization/catalog prueba que existe su PermissionDefinition, y que una llamada
    /// autorizada devuelva 200/201 y no 500 prueba que existe su AddPolicy — la otra mitad, que
    /// se registra a mano y por separado. Mismo criterio que
    /// TaxRatePermissionsArePublishedAndTheirPoliciesResolve.
    /// </summary>
    [Fact]
    public async Task ClassificationPermissionsArePublishedAndTheirPoliciesResolve()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(
            factory,
            SubjectId,
            TenantId,
            CustomersPermissions.ClassificationRead,
            CustomersPermissions.ClassificationManage,
            "tenancy.membership.read");

        var catalog = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/authorization/catalog",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        var body = await catalog.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(CustomersPermissions.ClassificationRead, body, StringComparison.Ordinal);
        Assert.Contains(CustomersPermissions.ClassificationManage, body, StringComparison.Ordinal);

        var list = await client.GetAsync(
            ClassificationsUrl(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var created = await CreateClassificationAsync(client, TenantId, "Mayorista", "MAY");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task CreateWritesExactlyOneAuditEventToTheOutbox()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClassificationManager(factory);

        var created = await ReadClassificationAsync(
            await CreateClassificationAsync(client, TenantId, "Mayorista", "MAY"));

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var events = await QueryAuditEventsAsync(connection, "customers.classification.created");
        var single = Assert.Single(events);
        Assert.Contains(TenantId, single, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(SubjectId, single, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(created.Id.ToString(), single, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateActivateDeactivateAndDeleteEachWriteExactlyOneAuditEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClassificationManager(factory);
        var created = await ReadClassificationAsync(
            await CreateClassificationAsync(client, TenantId, "Mayorista", "MAY"));

        (await client.PutAsJsonAsync(
            $"{ClassificationsUrl()}/{created.Id}",
            new { name = "Minorista", prefix = "MIN" },
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsync(
            $"{ClassificationsUrl()}/{created.Id}/deactivate",
            content: null,
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.PostAsync(
            $"{ClassificationsUrl()}/{created.Id}/activate",
            content: null,
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        (await client.DeleteAsync(
            $"{ClassificationsUrl()}/{created.Id}",
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        Assert.Single(await QueryAuditEventsAsync(connection, "customers.classification.updated"));
        Assert.Single(
            await QueryAuditEventsAsync(connection, "customers.classification.deactivated"));
        Assert.Single(await QueryAuditEventsAsync(connection, "customers.classification.activated"));
        Assert.Single(await QueryAuditEventsAsync(connection, "customers.classification.deleted"));
    }

    private static Task<HttpResponseMessage> CreateClassificationAsync(
        HttpClient client,
        string tenantId,
        string name,
        string prefix) =>
        client.PostAsJsonAsync(
            ClassificationsUrl(tenantId),
            new { name, prefix },
            TestContext.Current.CancellationToken);

    private static async Task<ClientClassificationResponse> ReadClassificationAsync(
        HttpResponseMessage response)
    {
        var classification = await response.Content.ReadFromJsonAsync<ClientClassificationResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(classification);
        return classification;
    }

    private static async Task<IReadOnlyCollection<ClientClassificationResponse>> ListAsync(
        HttpClient client,
        string tenantId)
    {
        var response = await client.GetAsync(
            ClassificationsUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ClientClassificationsResponse>(
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
}
