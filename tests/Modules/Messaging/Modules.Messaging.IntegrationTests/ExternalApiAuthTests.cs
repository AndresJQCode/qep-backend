using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Modules.ApiAccess.Application;
using Modules.ApiAccess.Domain;
using Modules.Messaging.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>
/// Spec 2026-10-30 §4.3 y §5.2: el esquema <c>ApiKey</c>, el grupo <c>/api/v1/external</c> y sus dos lecturas. Es la frontera de
/// seguridad de la función: cada rechazo se comprueba por HTTP y contra la base real.
/// </summary>
public sealed class ExternalApiAuthTests
{
    private static readonly string[] SendScope = [MessagingScopes.TemplatesSend];

    // 43 caracteres de secreto: tiene la forma de una clave, así que cae en Unknown y no en Malformed.
    private const string InventedKey = "qep_abcd1234_inventada" + "0000000000000000000000000000000000";

    private const string TemplatesPage = """
        {"data":[
          {"id":"1","name":"seguimiento","language":"es","status":"APPROVED","category":"UTILITY","parameter_format":"NAMED",
           "components":[{"type":"BODY","text":"Hola {{first_name}}, ¿sigues interesado?"}]},
          {"id":"2","name":"borrador","language":"es","status":"REJECTED","category":"MARKETING","components":[{"type":"BODY","text":"x"}]},
          {"id":"3","name":"promo","language":"es","status":"APPROVED","category":"MARKETING",
           "components":[{"type":"HEADER","format":"IMAGE"},{"type":"BODY","text":"Mira"}]}
        ]}
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(
        QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid MemberId, Guid ConnectionId);

    private static async Task<Fixture> ArrangeAsync(
        TestDatabase database, bool developer = true, bool messaging = true)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        if (developer)
        {
            await EnableDeveloperAsync(connectionString, tenant.TenantId);
        }

        if (messaging)
        {
            await EnableMessagingAsync(connectionString, tenant.TenantId);
        }

        var member = await OwnerMembershipIdAsync(connectionString, tenant);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        return new Fixture(factory, connectionString, tenant, member, connectionId);
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static string WithoutTraceId(JsonElement body) =>
        JsonSerializer.Serialize(body.EnumerateObject()
            .Where(property => property.Name != "traceId")
            .ToDictionary(property => property.Name, property => property.Value));

    [Fact]
    public void TheInventedKeyHasTheShapeOfARealOne() =>
        Assert.True(ApiKeyMaterial.TryParse(InventedKey, out _, out _));

    [Theory]
    [InlineData(null)]
    [InlineData("basura")]
    [InlineData(InventedKey)]
    public async Task AMissingMalformedOrUnknownKeyGetsTheSame401(string? key)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var client = CreateExternalClient(f.Factory, key);

        var response = await client.GetAsync(ExternalConnectionsUrl, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await BodyAsync(response);
        Assert.Equal("external.auth.invalid_key", body.GetProperty("code").GetString());
        Assert.Equal("Autenticación requerida", body.GetProperty("title").GetString());
        Assert.Equal(401, body.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
    }

    // El prefijo es público (la lista de claves lo devuelve): lo único que impide entrar con él es la comparación del secreto.
    private static string WithWrongSecret(string plaintext) => plaintext[..13] + new string('A', 43);

    [Fact]
    public async Task AKnownPrefixWithAWrongSecretIs401()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var (_, plaintext) = await SeedApiKeyAsync(f.ConnectionString, f.Tenant.TenantId, f.MemberId, SendScope);
        var wrong = WithWrongSecret(plaintext);
        Assert.NotEqual(plaintext, wrong);
        Assert.True(ApiKeyMaterial.TryParse(wrong, out var prefix, out var wrongSecret));
        Assert.Equal(plaintext.Substring(4, 8), prefix);
        using var client = CreateExternalClient(f.Factory, wrong);

        var response = await client.GetAsync(ExternalConnectionsUrl, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("external.auth.invalid_key", (await BodyAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task TheFourBodiesAreEqualExceptForTheTraceId()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var (_, plaintext) = await SeedApiKeyAsync(f.ConnectionString, f.Tenant.TenantId, f.MemberId, SendScope);
        var bodies = new List<string>();
        foreach (var key in new string?[] { null, "basura", InventedKey, WithWrongSecret(plaintext) })
        {
            using var client = CreateExternalClient(f.Factory, key);
            var response = await client.GetAsync(ExternalConnectionsUrl, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            bodies.Add(WithoutTraceId(await BodyAsync(response)));
        }

        Assert.Single(bodies.Distinct());
    }

    [Fact]
    public async Task TheNextRequestAfterRevokingIs401()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var (id, plaintext) = await SeedApiKeyAsync(f.ConnectionString, f.Tenant.TenantId, f.MemberId, SendScope);
        using var client = CreateExternalClient(f.Factory, plaintext);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(ExternalConnectionsUrl, Ct)).StatusCode);

        await ExecuteAsync(
            f.ConnectionString,
            "UPDATE api_access.api_keys SET revoked_at = now(), revoked_by_member_id = @member WHERE id = @id",
            ("member", f.MemberId), ("id", id));

        var response = await client.GetAsync(ExternalConnectionsUrl, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("external.auth.invalid_key", (await BodyAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task TheNextRequestAfterExpiringIs401()
    {
        await using var database = await StartDatabaseAsync();
        var clock = new TestClock { UtcNow = DateTimeOffset.UtcNow };
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var clocked = f.Factory.WithTestClock(clock);
        var (_, plaintext) = await SeedApiKeyAsync(
            f.ConnectionString, f.Tenant.TenantId, f.MemberId, SendScope, expiresAt: clock.UtcNow.AddHours(1));
        using var client = CreateExternalClient(clocked, plaintext);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(ExternalConnectionsUrl, Ct)).StatusCode);

        clock.UtcNow = clock.UtcNow.AddHours(2);

        var response = await client.GetAsync(ExternalConnectionsUrl, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("external.auth.invalid_key", (await BodyAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AKeyOfASuspendedTenantIs401()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var (_, plaintext) = await SeedApiKeyAsync(f.ConnectionString, f.Tenant.TenantId, f.MemberId, SendScope);
        using var client = CreateExternalClient(f.Factory, plaintext);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(ExternalConnectionsUrl, Ct)).StatusCode);

        await using (var scope = f.Factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            var tenant = dbContext.Tenants.Single(value => value.Id == new TenantId(f.Tenant.TenantId));
            tenant.Suspend(ChangeReason.Nonpayment, DateTimeOffset.UtcNow);
            await dbContext.SaveChangesAsync(Ct);
        }

        var response = await client.GetAsync(ExternalConnectionsUrl, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("external.auth.invalid_key", (await BodyAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task TwoHeaderValuesAreRejected()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var (_, plaintext) = await SeedApiKeyAsync(f.ConnectionString, f.Tenant.TenantId, f.MemberId, SendScope);
        using var client = f.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, ExternalConnectionsUrl);
        request.Headers.Add("X-Api-Key", [plaintext, plaintext]);

        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("external.auth.invalid_key", (await BodyAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AKeyWithoutTheScopeIs403WithItsCode()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var (_, plaintext) = await SeedApiKeyAsync(f.ConnectionString, f.Tenant.TenantId, f.MemberId, ["other.scope"]);
        using var client = CreateExternalClient(f.Factory, plaintext);

        var response = await client.GetAsync(ExternalConnectionsUrl, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await BodyAsync(response);
        Assert.Equal("external.auth.scope_missing", body.GetProperty("code").GetString());
        Assert.Equal("Acceso denegado", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task HandlerRevalidatesTheScopeWhenCalledDirectly()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await using var scope = f.Factory.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<ListExternalConnectionsHandler>(
            scope.ServiceProvider, new ScopelessCaller(f.Tenant.TenantId));

        var thrown = await Assert.ThrowsAsync<RequestForbiddenException>(
            () => handler.HandleAsync(new ListExternalConnectionsQuery(), Ct));

        Assert.Equal("external.auth.scope_missing", thrown.Code);
    }

    [Fact]
    public async Task DeveloperOffIs403ModuleNotEnabled()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, developer: false);
        using var _ = f.Factory;
        var (_, plaintext) = await SeedApiKeyAsync(f.ConnectionString, f.Tenant.TenantId, f.MemberId, SendScope);
        using var client = CreateExternalClient(f.Factory, plaintext);

        var response = await client.GetAsync(ExternalConnectionsUrl, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await BodyAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task MessagingOffIs403ModuleNotEnabled()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, messaging: false);
        using var _ = f.Factory;
        var (_, plaintext) = await SeedApiKeyAsync(f.ConnectionString, f.Tenant.TenantId, f.MemberId, SendScope);
        using var client = CreateExternalClient(f.Factory, plaintext);

        var response = await client.GetAsync(ExternalConnectionsUrl, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await BodyAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task TheApiWorksWithTheDevelopmentStubOn()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var (_, plaintext) = await SeedApiKeyAsync(f.ConnectionString, f.Tenant.TenantId, f.MemberId, SendScope);
        using var client = CreateExternalClient(f.Factory, plaintext);

        var response = await client.GetAsync(ExternalConnectionsUrl, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ConnectionsOnlyListTheNumbersTheKeyMayUse()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var second = await SeedWhatsAppConnectionAsync(f.Factory, f.Tenant.TenantId, "Atención", "333", "444");
        await SeedInstagramConnectionAsync(f.Factory, f.Tenant.TenantId, "Instagram", "555");
        var (_, restricted) = await SeedApiKeyAsync(
            f.ConnectionString, f.Tenant.TenantId, f.MemberId, SendScope, connectionIds: [second], name: "Acotada");
        var (_, open) = await SeedApiKeyAsync(f.ConnectionString, f.Tenant.TenantId, f.MemberId, SendScope, name: "Abierta");

        using var restrictedClient = CreateExternalClient(f.Factory, restricted);
        using var openClient = CreateExternalClient(f.Factory, open);
        var restrictedList = (await BodyAsync(await restrictedClient.GetAsync(ExternalConnectionsUrl, Ct))).EnumerateArray().ToArray();
        var openList = (await BodyAsync(await openClient.GetAsync(ExternalConnectionsUrl, Ct))).EnumerateArray().ToArray();

        var only = Assert.Single(restrictedList);
        Assert.Equal(second, only.GetProperty("id").GetGuid());
        Assert.Equal("Atención", only.GetProperty("name").GetString());
        Assert.Equal("+57 300 123 4567", only.GetProperty("phoneNumber").GetString());
        Assert.Equal(["Atención", "Ventas"], openList.Select(item => item.GetProperty("name").GetString()).Order());
    }

    [Fact]
    public async Task TemplatesOnlyListSendableOnes()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        f.Factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, TemplatesPage);
        var (_, plaintext) = await SeedApiKeyAsync(f.ConnectionString, f.Tenant.TenantId, f.MemberId, SendScope);
        using var client = CreateExternalClient(f.Factory, plaintext);

        var response = await client.GetAsync(ExternalTemplatesUrl(f.ConnectionId), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var template = Assert.Single((await BodyAsync(response)).EnumerateArray().ToArray());
        Assert.Equal("seguimiento", template.GetProperty("name").GetString());
        Assert.Equal("es", template.GetProperty("language").GetString());
        Assert.Equal("UTILITY", template.GetProperty("category").GetString());
        Assert.Equal(["first_name"], template.GetProperty("parameters").EnumerateArray().Select(item => item.GetString()));
        Assert.False(template.TryGetProperty("body", out var ignored));
    }

    [Fact]
    public async Task AConnectionOfAnotherTenantIs403()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var other = await RegisterTenantAsync(f.Factory);
        var foreign = await SeedWhatsAppConnectionAsync(f.Factory, other.TenantId, "Ajena", "777", "888");
        var ownSecond = await SeedWhatsAppConnectionAsync(f.Factory, f.Tenant.TenantId, "Propia", "999", "000");
        var (_, openKey) = await SeedApiKeyAsync(f.ConnectionString, f.Tenant.TenantId, f.MemberId, SendScope, name: "Abierta");
        var (_, restrictedKey) = await SeedApiKeyAsync(
            f.ConnectionString, f.Tenant.TenantId, f.MemberId, SendScope, connectionIds: [f.ConnectionId], name: "Acotada");

        using var open = CreateExternalClient(f.Factory, openKey);
        using var restricted = CreateExternalClient(f.Factory, restrictedKey);
        var foreignWithNullList = await open.GetAsync(ExternalTemplatesUrl(foreign), Ct);
        var foreignWithList = await restricted.GetAsync(ExternalTemplatesUrl(foreign), Ct);
        var ownOutsideList = await restricted.GetAsync(ExternalTemplatesUrl(ownSecond), Ct);

        foreach (var response in new[] { foreignWithNullList, foreignWithList, ownOutsideList })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("external.connection_not_allowed", (await BodyAsync(response)).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task TemplatesOfAnInstagramConnectionAre422()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var instagram = await SeedInstagramConnectionAsync(f.Factory, f.Tenant.TenantId, "Instagram", "555");
        var (_, plaintext) = await SeedApiKeyAsync(f.ConnectionString, f.Tenant.TenantId, f.MemberId, SendScope);
        using var client = CreateExternalClient(f.Factory, plaintext);

        var response = await client.GetAsync(ExternalTemplatesUrl(instagram), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.channel_unsupported", (await BodyAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task LastUsedIsWrittenAndAFailureDoesNotBreakTheRequest()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var (id, plaintext) = await SeedApiKeyAsync(f.ConnectionString, f.Tenant.TenantId, f.MemberId, SendScope);
        using (var client = CreateExternalClient(f.Factory, plaintext))
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(ExternalConnectionsUrl, Ct)).StatusCode);
        }

        Assert.Equal(
            1L,
            await CountAsync(f.ConnectionString, "SELECT count(*) FROM api_access.api_keys WHERE id = @id AND last_used_at IS NOT NULL", ("id", id)));

        // Segundo caso: el repositorio decorado lanza al marcar el último uso; la clave sigue siendo válida y la petición sale 200.
        var logs = new CapturedLogs();
        using var failing = f.Factory.WithCapturedLogs(logs).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            var descriptor = services.Single(item => item.ServiceType == typeof(IApiKeyRepository));
            services.Remove(descriptor);
            services.AddScoped<IApiKeyRepository>(provider => new TouchThrowsRepository(
                (IApiKeyRepository)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!)));
        }));
        using var failingClient = CreateExternalClient(failing, plaintext);

        var response = await failingClient.GetAsync(ExternalConnectionsUrl, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("last_used", logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(plaintext, logs.AllText, StringComparison.Ordinal);
    }

    private sealed class ScopelessCaller(Guid tenantId) : IApiCaller
    {
        public Guid ApiKeyId => Guid.Empty;

        public string ApiKeyName => "sin scope";

        public Guid TenantId => tenantId;

        public bool HasScope(string scope) => false;

        public bool CanUseConnection(Guid connectionId) => true;
    }

    private sealed class TouchThrowsRepository(IApiKeyRepository inner) : IApiKeyRepository
    {
        public void Add(ApiKey key) => inner.Add(key);

        public Task<ApiKey?> FindAsync(Guid tenantId, ApiKeyId id, CancellationToken cancellationToken) =>
            inner.FindAsync(tenantId, id, cancellationToken);

        public Task<IReadOnlyList<ApiKey>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            inner.ListAsync(tenantId, cancellationToken);

        public Task<int> CountActiveAsync(Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.CountActiveAsync(tenantId, now, cancellationToken);

        public Task<ApiKey?> FindByPrefixAsync(string keyPrefix, CancellationToken cancellationToken) =>
            inner.FindByPrefixAsync(keyPrefix, cancellationToken);

        public Task TouchLastUsedAsync(ApiKeyId id, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }
}
