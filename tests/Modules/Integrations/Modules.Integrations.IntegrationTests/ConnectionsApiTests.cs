using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Integrations.Application;
using static Modules.Integrations.IntegrationTests.IntegrationsApiHarness;

namespace Modules.Integrations.IntegrationTests;

/// <summary>
/// Spec 2026-10-08, «Endpoints» y «Códigos de error», por HTTP: crear con prueba contra Zenvia, el
/// contrato JSON en camelCase, cada código con su status, aislamiento y visibilidad por módulo.
/// </summary>
public sealed class ConnectionsApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Criterio 2 del spec.
    [Fact]
    public async Task AnAdminConnectsZenviaAndTheCatalogCountsIt()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var response = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), ZenviaBody());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync(Ct);
        // El contrato del spec, literal: camelCase, enums por nombre, claves de diccionario tal cual.
        using (var document = JsonDocument.Parse(json))
        {
            var root = document.RootElement;
            Assert.Equal("zenvia", root.GetProperty("providerKey").GetString());
            Assert.Equal("Active", root.GetProperty("status").GetString());
            Assert.Equal(FromNumber, root.GetProperty("fields").GetProperty("fromNumber").GetString());
            Assert.True(root.GetProperty("secrets").GetProperty("apiToken").GetProperty("configured").GetBoolean());
            Assert.True(root.GetProperty("secrets").GetProperty("apiToken").GetProperty("readable").GetBoolean());
            Assert.Equal(1, root.GetProperty("version").GetInt64());
            Assert.True(root.GetProperty("createdBy").TryGetProperty("memberId", out _));
        }

        var created = JsonSerializer.Deserialize<ConnectionResponse>(json, JsonSerializerOptions.Web);
        Assert.NotNull(created);
        Assert.Equal(ConnectionUrl(tenant.TenantId, created.Id), response.Headers.Location?.OriginalString);
        Assert.Equal(await OwnerMembershipIdAsync(connectionString, tenant), created.CreatedBy.MemberId);
        Assert.Equal(tenant.Email, created.CreatedBy.DisplayName);
        Assert.NotNull(created.LastVerifiedAt);

        var request = Assert.Single(factory.ZenviaHandler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"{ZenviaBaseUrl}/v2/templates", request.Uri?.ToString());
        Assert.Equal(SentinelApiToken, request.Token);
        Assert.Contains("qep-integrations", request.UserAgent, StringComparison.Ordinal);

        var catalog = await client.GetFromJsonAsync<IntegrationsCatalogResponse>(CatalogUrl(tenant.TenantId), Ct);
        Assert.Equal(1, Assert.Single(catalog!.Providers).ConnectionCount);
        var list = await client.GetFromJsonAsync<ConnectionsResponse>(ConnectionsUrl(tenant.TenantId), Ct);
        Assert.Equal(created.Id, Assert.Single(list!.Items).Id);
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM audit.entries WHERE action = 'integrations.connection.created' AND tenant_id = @tenantId",
            ("tenantId", tenant.TenantId)));
    }

    // F2 del plan de frontend: credentials_rejected trae errors con la clave del secreto.
    [Fact]
    public async Task RejectedCredentialsAnswer422OnTheSecretFieldAndSaveNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        factory.ZenviaHandler.Status = HttpStatusCode.Unauthorized;

        var response = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), ZenviaBody());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var (code, errorKeys) = await ProblemAsync(response);
        Assert.Equal("integrations.connection.credentials_rejected", code);
        Assert.Equal(["secrets.apiToken"], errorKeys);
        Assert.Equal(0L, await CountConnectionsAsync(database.GetConnectionString(), tenant.TenantId));
    }

    // D3: «no pude verificar» bloquea y no marca ningún campo.
    [Fact]
    public async Task AnUnreachableProviderAnswers422WithoutAFieldAndSavesNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        factory.ZenviaHandler.Throw = new TaskCanceledException("timed out", new TimeoutException());

        var response = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), ZenviaBody());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var (code, errorKeys) = await ProblemAsync(response);
        Assert.Equal("integrations.connection.provider_unreachable", code);
        Assert.Empty(errorKeys);
        Assert.Equal(0L, await CountConnectionsAsync(database.GetConnectionString(), tenant.TenantId));
    }

    // F1 del plan de frontend y Review Focus 1-2: claves exactas en minúscula, nunca un 500, sin
    // llamar a Zenvia y sin fila.
    [Fact]
    public async Task ValidationIsPerFieldWithExactKeysAndNeverAServerError()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var response = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), new
        {
            providerKey = "zenvia",
            name = "a\u0000b",
            fields = new Dictionary<string, string?> { ["fromNumber"] = "+573001234567", ["apiToken"] = SentinelApiToken },
            secrets = new Dictionary<string, string?>(),
        });
        var unknownProvider = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), new
        {
            providerKey = "whatsapp",
            name = "Norte",
            fields = new Dictionary<string, string?>(),
            secrets = new Dictionary<string, string?>(),
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var (code, errorKeys) = await ProblemAsync(response);
        Assert.Equal("validation.failed", code);
        Assert.Equal(["fields.apiToken", "fields.fromNumber", "name", "secrets.apiToken"], errorKeys);
        Assert.Equal(["providerKey"], (await ProblemAsync(unknownProvider)).ErrorKeys);
        Assert.Empty(factory.ZenviaHandler.Requests);
        Assert.Equal(0L, await CountConnectionsAsync(database.GetConnectionString(), tenant.TenantId));
    }

    // Review Focus 5.
    [Fact]
    public async Task ANameRepeatedWithOtherCaseIsNameTakenOnlyInsideTheTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory);
        var other = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        using var otherClient = CreateClient(factory, other.OwnerUserId, other.TenantId, ManagePermissions);
        await CreateConnectionAsync(client, tenant.TenantId, "WhatsApp Norte");

        var repeated = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), ZenviaBody("  whatsapp norte  "));
        var elsewhere = await SendAsync(otherClient, HttpMethod.Post, ConnectionsUrl(other.TenantId), ZenviaBody("WhatsApp Norte"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, repeated.StatusCode);
        Assert.Equal("integrations.connection.name_taken", (await ProblemAsync(repeated)).Code);
        Assert.Equal(1L, await CountConnectionsAsync(database.GetConnectionString(), tenant.TenantId));
        Assert.Equal(HttpStatusCode.Created, elsewhere.StatusCode);
    }

    // D1.
    [Fact]
    public async Task TheTwentyFirstConnectionIsLimitReached()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        for (var index = 1; index <= 20; index++)
        {
            await CreateConnectionAsync(client, tenant.TenantId, $"Línea {index}");
        }

        var response = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), ZenviaBody("Línea 21"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("integrations.connection.limit_reached", (await ProblemAsync(response)).Code);
        Assert.Equal(20, factory.ZenviaHandler.Requests.Count);
    }

    [Fact]
    public async Task AnotherTenantIs403TheIdIs404InsideItsOwnRouteAndAReaderCannotWrite()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var a = await RegisterTenantAsync(factory);
        var b = await RegisterTenantAsync(factory);
        using var ownerA = CreateClient(factory, a.OwnerUserId, a.TenantId, ManagePermissions);
        using var ownerB = CreateClient(factory, b.OwnerUserId, b.TenantId, ManagePermissions);
        using var readerA = CreateClient(factory, a.OwnerUserId, a.TenantId, ReadPermissions);
        var created = await CreateConnectionAsync(ownerA, a.TenantId);

        var crossRead = await SendAsync(ownerB, HttpMethod.Get, ConnectionUrl(a.TenantId, created.Id));
        var crossWrite = await SendAsync(ownerB, HttpMethod.Post, ConnectionsUrl(a.TenantId), ZenviaBody("Intrusa"));
        var foreignId = await SendAsync(ownerB, HttpMethod.Get, ConnectionUrl(b.TenantId, created.Id));
        var readerWrite = await SendAsync(readerA, HttpMethod.Post, ConnectionsUrl(a.TenantId), ZenviaBody("Otra"));

        Assert.Equal(HttpStatusCode.Forbidden, crossRead.StatusCode);
        Assert.Equal("authorization.denied", (await ProblemAsync(crossRead)).Code);
        Assert.Equal(HttpStatusCode.Forbidden, crossWrite.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignId.StatusCode);
        Assert.Equal("integrations.connection.not_found", (await ProblemAsync(foreignId)).Code);
        Assert.Equal(HttpStatusCode.Forbidden, readerWrite.StatusCode);
        Assert.Equal(1L, await CountConnectionsAsync(database.GetConnectionString(), a.TenantId));
    }

    // Criterio 6 del spec.
    [Fact]
    public async Task TurningOffTheConsumingModuleHidesTheConnectionAndTurningItOnBringsItBackIntact()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var created = await CreateConnectionAsync(client, tenant.TenantId);

        await SetModuleStatusAsync(connectionString, tenant.TenantId, "quotations", "inactive");

        var catalog = await client.GetFromJsonAsync<IntegrationsCatalogResponse>(CatalogUrl(tenant.TenantId), Ct);
        var list = await client.GetFromJsonAsync<ConnectionsResponse>(ConnectionsUrl(tenant.TenantId), Ct);
        var hidden = await SendAsync(client, HttpMethod.Get, ConnectionUrl(tenant.TenantId, created.Id));
        var newOne = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), ZenviaBody("Otra"));
        Assert.Empty(catalog!.Providers);
        Assert.Empty(list!.Items);
        Assert.Equal(HttpStatusCode.Forbidden, hidden.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(hidden)).Code);
        Assert.Equal(HttpStatusCode.Forbidden, newOne.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(newOne)).Code);
        Assert.Equal(1L, await CountConnectionsAsync(connectionString, tenant.TenantId));

        await SetModuleStatusAsync(connectionString, tenant.TenantId, "quotations", "active");

        var back = await client.GetFromJsonAsync<ConnectionResponse>(ConnectionUrl(tenant.TenantId, created.Id), Ct);
        Assert.Equal(created.Version, back!.Version);
        Assert.Equal("Active", back.Status);
        Assert.True(back.Secrets["apiToken"].Readable);
    }
}
