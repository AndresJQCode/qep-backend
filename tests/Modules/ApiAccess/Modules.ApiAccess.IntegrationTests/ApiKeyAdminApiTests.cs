using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.ApiAccess.Application;

namespace Modules.ApiAccess.IntegrationTests;

public sealed class ApiKeyAdminApiTests
{
    private const string TemplatesSend = "whatsapp.templates.send";

    private static readonly Guid OwnConnectionA = Guid.CreateVersion7();
    private static readonly Guid OwnConnectionB = Guid.CreateVersion7();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Route(Guid tenantId, string suffix = "") =>
        $"/api/v1/tenants/{tenantId}/api-keys{suffix}";

    /// <summary>
    /// El doble de las conexiones del tenant: dos propias. Las de otro tenant no están en la lista, que es
    /// justo lo que el adaptador real garantiza al consultar por tenant.
    /// </summary>
    private sealed class FakeConnectionDirectory : IApiKeyConnectionDirectory
    {
        public Task<IReadOnlyDictionary<Guid, string>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>
            {
                [OwnConnectionA] = "Ventas",
                [OwnConnectionB] = "Soporte",
            });
    }

    private static void UseFakeConnections(IServiceCollection services)
    {
        services.RemoveAll<IApiKeyConnectionDirectory>();
        services.AddSingleton<IApiKeyConnectionDirectory, FakeConnectionDirectory>();
    }

    /// <summary>Un catálogo sin ningún scope restringible por conexión.</summary>
    private static void UseCatalogWithoutRestrictableScopes(IServiceCollection services)
    {
        UseFakeConnections(services);
        services.RemoveAll<ApiScopeDefinition>();
        services.AddSingleton(new ApiScopeDefinition(
            TemplatesSend, "Enviar plantillas", "Solo para la prueba.", "WhatsApp", "messaging",
            RestrictableByConnection: false));
    }

    private static async Task<(Testcontainers.PostgreSql.PostgreSqlContainer Database, ApiAccessApiHarness.QepApiFactory Factory,
        string ConnectionString, ApiAccessApiHarness.RegisteredTenant Tenant, HttpClient Admin)> OpenAsync(
        Action<IServiceCollection>? configure = null,
        bool messaging = true,
        bool developer = true)
    {
        var (database, factory) = await ApiAccessApiHarness.OpenAsync(configure ?? UseFakeConnections);
        var connectionString = database.GetConnectionString();
        var tenant = await ApiAccessApiHarness.RegisterTenantAsync(factory);
        if (developer)
        {
            await ApiAccessApiHarness.EnableDeveloperAsync(connectionString, tenant.TenantId);
        }

        if (messaging)
        {
            await ApiAccessApiHarness.EnableModuleAsync(connectionString, tenant.TenantId, "messaging");
        }

        var admin = ApiAccessApiHarness.CreateClient(
            factory, tenant.OwnerUserId, tenant.TenantId, ApiAccessPermissions.ApiKeyManage);
        return (database, factory, connectionString, tenant, admin);
    }

    private static Task<HttpResponseMessage> CreateAsync(
        HttpClient client, Guid tenantId, string name, string[]? scopes = null, Guid[]? connectionIds = null,
        DateTimeOffset? expiresAt = null) =>
        client.PostAsJsonAsync(
            Route(tenantId),
            new { name, scopes = scopes ?? [TemplatesSend], connectionIds, expiresAt },
            Token);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        return document.RootElement.Clone();
    }

    private static async Task<(Guid Id, string Secret)> CreateOkAsync(
        HttpClient client, Guid tenantId, string name, DateTimeOffset? expiresAt = null)
    {
        var response = await CreateAsync(client, tenantId, name, expiresAt: expiresAt);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await JsonAsync(response);
        return (body.GetProperty("key").GetProperty("id").GetGuid(), body.GetProperty("secret").GetString()!);
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString());
    }

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("validation.failed", body.GetProperty("code").GetString());
        return body.GetProperty("errors").EnumerateObject().Select(property => property.Name).Order().ToArray();
    }

    private static Task<long> AuditCountAsync(string connectionString, Guid tenantId, string action) =>
        ApiAccessApiHarness.ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM audit.entries WHERE tenant_id = @tenantId AND action = @action",
            ("tenantId", tenantId),
            ("action", action));

    [Fact]
    public async Task CreateReturnsTheSecretOnlyInThe201()
    {
        var (database, factory, _, tenant, admin) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var response = await CreateAsync(admin, tenant.TenantId, "ERP");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await JsonAsync(response);
        var secret = body.GetProperty("secret").GetString()!;
        Assert.StartsWith("qep_", secret, StringComparison.Ordinal);
        var key = body.GetProperty("key");
        Assert.Equal("ERP", key.GetProperty("name").GetString());
        Assert.Equal("Active", key.GetProperty("status").GetString());
        Assert.Equal(secret.Split('_')[1], key.GetProperty("keyPrefix").GetString());
        Assert.Equal(JsonValueKind.Null, key.GetProperty("connections").ValueKind);
        var scope = Assert.Single(key.GetProperty("scopes").EnumerateArray());
        Assert.Equal(TemplatesSend, scope.GetProperty("key").GetString());
        Assert.Equal("Enviar plantillas", scope.GetProperty("name").GetString());

        var list = await admin.GetAsync(Route(tenant.TenantId), Token);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var text = await list.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListCalculatesStatusAndNeverExposesTheHash()
    {
        var (database, factory, connectionString, tenant, admin) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var now = DateTimeOffset.UtcNow;

        await CreateOkAsync(admin, tenant.TenantId, "Activa");
        await CreateOkAsync(admin, tenant.TenantId, "Por vencer", now.AddDays(10));
        await CreateOkAsync(admin, tenant.TenantId, "Vencida", now.AddDays(90));
        var revoked = await CreateOkAsync(admin, tenant.TenantId, "Revocada");
        // Una clave no se puede crear ya vencida: se envejece directo en la base.
        await ApiAccessApiHarness.ExecuteAsync(
            connectionString,
            "UPDATE api_access.api_keys SET expires_at = now() - interval '1 day' WHERE name = 'Vencida'");
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync(Route(tenant.TenantId, $"/{revoked.Id}/revoke"), null, Token)).StatusCode);

        var response = await admin.GetAsync(Route(tenant.TenantId), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("hash", text, StringComparison.OrdinalIgnoreCase);
        var statuses = (await JsonAsync(response)).EnumerateArray()
            .ToDictionary(key => key.GetProperty("name").GetString()!, key => key.GetProperty("status").GetString());
        Assert.Equal("Active", statuses["Activa"]);
        Assert.Equal("ExpiringSoon", statuses["Por vencer"]);
        Assert.Equal("Expired", statuses["Vencida"]);
        Assert.Equal("Revoked", statuses["Revocada"]);
        // Más nueva primero.
        Assert.Equal(
            ["Revocada", "Vencida", "Por vencer", "Activa"],
            (await JsonAsync(response)).EnumerateArray().Select(key => key.GetProperty("name").GetString()!).ToArray());
    }

    [Fact]
    public async Task ListShowsConnectionNamesAndMarksADeletedOneWithNullName()
    {
        var (database, factory, _, tenant, admin) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var deleted = Guid.CreateVersion7();
        // La clave se creó con una conexión que el directorio ya no lista: se siembra por la API con una propia
        // y se cambia el id en la base para simular el borrado físico de la conexión.
        var created = await CreateAsync(admin, tenant.TenantId, "ERP", connectionIds: [OwnConnectionA, OwnConnectionB]);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await ApiAccessApiHarness.ExecuteAsync(
            database.GetConnectionString(),
            "UPDATE api_access.api_keys SET connection_ids = ARRAY[@a, @gone]::uuid[] WHERE name = 'ERP'",
            ("a", OwnConnectionA),
            ("gone", deleted));

        var connections = (await JsonAsync(await admin.GetAsync(Route(tenant.TenantId), Token)))[0].GetProperty("connections");

        Assert.Equal(2, connections.GetArrayLength());
        Assert.Equal("Ventas", connections[0].GetProperty("name").GetString());
        Assert.Equal(deleted, connections[1].GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, connections[1].GetProperty("name").ValueKind);
    }

    [Fact]
    public async Task ScopeCatalogOnlyListsScopesOfEnabledModules()
    {
        var (database, factory, connectionString, tenant, admin) = await OpenAsync(messaging: false);
        await using var _ = database;
        using var __ = factory;

        var off = await JsonAsync(await admin.GetAsync(Route(tenant.TenantId, "/scopes"), Token));
        Assert.Equal(0, off.GetProperty("categories").GetArrayLength());

        await ApiAccessApiHarness.EnableModuleAsync(connectionString, tenant.TenantId, "messaging");

        var on = await JsonAsync(await admin.GetAsync(Route(tenant.TenantId, "/scopes"), Token));
        var category = Assert.Single(on.GetProperty("categories").EnumerateArray());
        Assert.Equal("WhatsApp", category.GetProperty("category").GetString());
        var scope = Assert.Single(category.GetProperty("scopes").EnumerateArray());
        Assert.Equal(TemplatesSend, scope.GetProperty("key").GetString());
        Assert.True(scope.GetProperty("restrictableByConnection").GetBoolean());
    }

    [Fact]
    public async Task AScopeOfADisabledModuleIsRejected()
    {
        var (database, factory, _, tenant, admin) = await OpenAsync(messaging: false);
        await using var _ = database;
        using var __ = factory;

        var response = await CreateAsync(admin, tenant.TenantId, "ERP");

        Assert.Equal(["scopes"], await ErrorKeysAsync(response));
    }

    [Fact]
    public async Task AnEmptyConnectionListIsRejectedInsteadOfGrantingEveryNumber()
    {
        var (database, factory, _, tenant, admin) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var response = await CreateAsync(admin, tenant.TenantId, "ERP", connectionIds: []);

        Assert.Equal(["connectionIds"], await ErrorKeysAsync(response));
    }

    [Fact]
    public async Task ConnectionIdsNeedARestrictableScope()
    {
        var (database, factory, _, tenant, admin) = await OpenAsync(UseCatalogWithoutRestrictableScopes);
        await using var _ = database;
        using var __ = factory;

        var response = await CreateAsync(admin, tenant.TenantId, "ERP", connectionIds: [OwnConnectionA]);

        Assert.Equal(["connectionIds"], await ErrorKeysAsync(response));
    }

    [Fact]
    public async Task AConnectionOfAnotherTenantIs403()
    {
        var (database, factory, _, tenant, admin) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var response = await CreateAsync(admin, tenant.TenantId, "ERP", connectionIds: [OwnConnectionA, Guid.CreateVersion7()]);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, ExternalApiErrorCodes.ConnectionNotAllowed);
    }

    [Fact]
    public async Task ACreatedKeyKeepsTheOwnConnectionsWithTheirNames()
    {
        var (database, factory, _, tenant, admin) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var response = await CreateAsync(admin, tenant.TenantId, "ERP", connectionIds: [OwnConnectionB]);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var connection = Assert.Single((await JsonAsync(response)).GetProperty("key").GetProperty("connections").EnumerateArray());
        Assert.Equal(OwnConnectionB, connection.GetProperty("id").GetGuid());
        Assert.Equal("Soporte", connection.GetProperty("name").GetString());
    }

    [Fact]
    public async Task ThePastExpiryIsRejected()
    {
        var (database, factory, _, tenant, admin) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var response = await CreateAsync(admin, tenant.TenantId, "ERP", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.Equal(["expiresAt"], await ErrorKeysAsync(response));
    }

    [Fact]
    public async Task TheTwentySixthActiveKeyIsRejected()
    {
        var (database, factory, _, tenant, admin) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        for (var index = 1; index <= 25; index++)
        {
            await CreateOkAsync(admin, tenant.TenantId, $"Clave {index}");
        }

        var response = await CreateAsync(admin, tenant.TenantId, "Una más");

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, ApiAccessErrorCodes.KeyLimitReached);
    }

    [Fact]
    public async Task RevokingTwiceDoesNotFailAndAuditsOnce()
    {
        var (database, factory, connectionString, tenant, admin) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var key = await CreateOkAsync(admin, tenant.TenantId, "ERP");

        var first = await admin.PostAsync(Route(tenant.TenantId, $"/{key.Id}/revoke"), null, Token);
        var second = await admin.PostAsync(Route(tenant.TenantId, $"/{key.Id}/revoke"), null, Token);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstBody = await JsonAsync(first);
        var secondBody = await JsonAsync(second);
        Assert.Equal("Revoked", firstBody.GetProperty("status").GetString());
        Assert.Equal(firstBody.GetProperty("revokedAt").GetDateTimeOffset(), secondBody.GetProperty("revokedAt").GetDateTimeOffset());
        Assert.Equal(1, await AuditCountAsync(connectionString, tenant.TenantId, ApiKeyAuditActions.Revoked));
        Assert.Equal(
            "success",
            await ApiAccessApiHarness.ScalarAsync<string>(
                connectionString,
                "SELECT outcome FROM audit.entries WHERE tenant_id = @tenantId AND action = @action AND resource_id = @id",
                ("tenantId", tenant.TenantId),
                ("action", ApiKeyAuditActions.Revoked),
                ("id", key.Id.ToString())));
    }

    [Fact]
    public async Task CreateAuditsWithoutTheSecret()
    {
        var (database, factory, connectionString, tenant, admin) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var key = await CreateOkAsync(admin, tenant.TenantId, "ERP");

        Assert.Equal(1, await AuditCountAsync(connectionString, tenant.TenantId, ApiKeyAuditActions.Created));
        var row = await ApiAccessApiHarness.ScalarAsync<string>(
            connectionString,
            "SELECT to_jsonb(e)::text FROM audit.entries e WHERE tenant_id = @tenantId AND action = @action",
            ("tenantId", tenant.TenantId),
            ("action", ApiKeyAuditActions.Created));
        Assert.DoesNotContain(key.Secret, row, StringComparison.Ordinal);
        Assert.DoesNotContain(key.Secret.Split('_')[2], row, StringComparison.Ordinal);
        Assert.Contains(key.Id.ToString(), row, StringComparison.Ordinal);
        Assert.Contains("success", row, StringComparison.Ordinal);
        foreach (var field in new[] { "connectionIds", "expiresAt", "name", "scopes" })
        {
            Assert.Contains($"\"{field}\"", row, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task WithoutThePermissionTheRoutesAre403()
    {
        var (database, factory, _, tenant, admin) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var key = await CreateOkAsync(admin, tenant.TenantId, "ERP");
        using var noPermission = ApiAccessApiHarness.CreateClient(factory, tenant.OwnerUserId, tenant.TenantId);

        Assert.Equal(HttpStatusCode.Forbidden, (await noPermission.GetAsync(Route(tenant.TenantId), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await noPermission.GetAsync(Route(tenant.TenantId, "/scopes"), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CreateAsync(noPermission, tenant.TenantId, "Otra")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await noPermission.PostAsync(Route(tenant.TenantId, $"/{key.Id}/revoke"), null, Token)).StatusCode);
    }

    [Fact]
    public async Task APermissionHolderOfAnotherTenantIs403()
    {
        var (database, factory, _, tenant, _) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var other = await ApiAccessApiHarness.RegisterTenantAsync(factory);
        using var otherAdmin = ApiAccessApiHarness.CreateClient(
            factory, other.OwnerUserId, other.TenantId, ApiAccessPermissions.ApiKeyManage);

        var response = await otherAdmin.GetAsync(Route(tenant.TenantId), Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WithTheDeveloperModuleOffTheRoutesAre403()
    {
        var (database, factory, _, tenant, admin) = await OpenAsync(developer: false);
        await using var _ = database;
        using var __ = factory;

        // Con el módulo apagado la máscara de capacidades le quita el permiso al llamador, así que frena la
        // política: un 403 pelado, sin cuerpo (el handler queda de respaldo con tenancy.module_not_enabled).
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync(Route(tenant.TenantId), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CreateAsync(admin, tenant.TenantId, "ERP")).StatusCode);
    }

    [Fact]
    public async Task AKeyOfAnotherTenantIs404OnRevoke()
    {
        var (database, factory, connectionString, tenant, admin) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var key = await CreateOkAsync(admin, tenant.TenantId, "ERP");
        var other = await ApiAccessApiHarness.RegisterTenantAsync(factory);
        await ApiAccessApiHarness.EnableDeveloperAsync(connectionString, other.TenantId);
        using var otherAdmin = ApiAccessApiHarness.CreateClient(
            factory, other.OwnerUserId, other.TenantId, ApiAccessPermissions.ApiKeyManage);

        var foreign = await otherAdmin.PostAsync(Route(other.TenantId, $"/{key.Id}/revoke"), null, Token);
        var missing = await otherAdmin.PostAsync(Route(other.TenantId, $"/{Guid.CreateVersion7()}/revoke"), null, Token);

        // Indistinguible de un id que no existe, y la clave ajena sigue viva.
        await AssertProblemAsync(foreign, HttpStatusCode.NotFound, ApiAccessErrorCodes.KeyNotFound);
        await AssertProblemAsync(missing, HttpStatusCode.NotFound, ApiAccessErrorCodes.KeyNotFound);
        Assert.Equal(
            0,
            await ApiAccessApiHarness.ScalarAsync<long>(
                connectionString,
                "SELECT count(*) FROM api_access.api_keys WHERE id = @id AND revoked_at IS NOT NULL",
                ("id", key.Id)));
        Assert.Equal(0, await AuditCountAsync(connectionString, other.TenantId, ApiKeyAuditActions.Revoked));
    }
}
