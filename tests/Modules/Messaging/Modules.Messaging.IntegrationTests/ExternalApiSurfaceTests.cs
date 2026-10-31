using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>
/// Spec 2026-10-30 §5.5: lo que rodea a la API externa sin ser su lógica: la exención de CSRF, los dos límites de tasa y qué
/// fallas dejan fila en <c>platform.request_failures</c>. Todo con el stub apagado, porque en modo stub el middleware de CSRF ni
/// se registra. Aún no hay un <c>POST</c> real bajo <c>/api/v1/external/</c> (llega con el envío): las pruebas montan uno de
/// sonda sobre el mismo grupo, para ejercer la tubería de producción y no una copia.
/// </summary>
public sealed class ExternalApiSurfaceTests
{
    private const string ProbeUrl = "/api/v1/external/probe";

    private const string InventedKey = "qep_abcd1234_inventada" + "0000000000000000000000000000000000";

    private static readonly string[] SendScope = [MessagingScopes.TemplatesSend];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(WebApplicationFactory<Program> Factory, string ConnectionString, Guid TenantId, Guid MemberId);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, int? perKey = null, int? perIp = null)
    {
        var connectionString = database.GetConnectionString();
        var seeding = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(seeding);
        await EnableDeveloperAsync(connectionString, tenant.TenantId);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var member = await OwnerMembershipIdAsync(connectionString, tenant);
        var factory = seeding.WithRealAuthentication().WithWebHostBuilder(builder =>
        {
            if (perKey is { } key)
            {
                builder.UseSetting("ApiAccess:RateLimit:PerKeyPerMinute", key.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (perIp is { } ip)
            {
                builder.UseSetting("ApiAccess:RateLimit:PerIpPerMinute", ip.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            builder.ConfigureTestServices(services => services.AddTransient<IStartupFilter, ProbeStartupFilter>());
        });
        return new Fixture(factory, connectionString, tenant.TenantId, member);
    }

    private static Task<HttpResponseMessage> PostProbeAsync(HttpClient client, string path = ProbeUrl) =>
        client.PostAsync(path, JsonContent.Create(new { }), Ct);

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    [Fact]
    public async Task AnExternalPostNeedsNoCsrfHeaderWithTheStubOff()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var client = CreateExternalClient(f.Factory, null);

        var response = await PostProbeAsync(client);

        // El 401 es el de la clave; sin la exención sería el 403 «Missing required client header».
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("external.auth.invalid_key", (await BodyAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task TheExemptionDidNotWidenToInternalRoutes()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var client = f.Factory.CreateClient();

        var response = await client.PostAsync("/api/v1/auth/logout", content: null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Missing required client header.", (await BodyAsync(response)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task TheKeyLimitAnswers429WithCodeAndRetryAfter()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, perKey: 2);
        using var _ = f.Factory;
        var (_, plaintext) = await SeedApiKeyAsync(f.ConnectionString, f.TenantId, f.MemberId, SendScope);
        using var client = CreateExternalClient(f.Factory, plaintext);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(ExternalConnectionsUrl, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(ExternalConnectionsUrl, Ct)).StatusCode);
        var third = await client.GetAsync(ExternalConnectionsUrl, Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal("application/problem+json", third.Content.Headers.ContentType?.MediaType);
        var body = await BodyAsync(third);
        Assert.Equal("external.rate_limited", body.GetProperty("code").GetString());
        Assert.Equal(429, body.GetProperty("status").GetInt32());
        var retryAfter = Assert.Single(third.Headers.GetValues("Retry-After"));
        var seconds = int.Parse(retryAfter, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(seconds, 1, 60);
    }

    [Fact]
    public async Task TheKeyLimitIsPerKey()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, perKey: 2);
        using var _ = f.Factory;
        var (_, first) = await SeedApiKeyAsync(f.ConnectionString, f.TenantId, f.MemberId, SendScope, name: "Primera");
        var (_, second) = await SeedApiKeyAsync(f.ConnectionString, f.TenantId, f.MemberId, SendScope, name: "Segunda");
        using var firstClient = CreateExternalClient(f.Factory, first);
        using var secondClient = CreateExternalClient(f.Factory, second);

        await firstClient.GetAsync(ExternalConnectionsUrl, Ct);
        await firstClient.GetAsync(ExternalConnectionsUrl, Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await firstClient.GetAsync(ExternalConnectionsUrl, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await secondClient.GetAsync(ExternalConnectionsUrl, Ct)).StatusCode);
    }

    [Fact]
    public async Task TheIpLimitStopsInventedKeysWith429AndCode()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, perIp: 3);
        using var _ = f.Factory;
        using var client = CreateExternalClient(f.Factory, InventedKey);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(ExternalConnectionsUrl, Ct)).StatusCode);
        }

        var fourth = await client.GetAsync(ExternalConnectionsUrl, Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, fourth.StatusCode);
        Assert.Equal("application/problem+json", fourth.Content.Headers.ContentType?.MediaType);
        Assert.Equal("external.rate_limited", (await BodyAsync(fourth)).GetProperty("code").GetString());
        Assert.True(fourth.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task A401AndA429OnTheExternalApiWriteNoFailureRow()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, perKey: 1, perIp: 4);
        using var _ = f.Factory;
        var (_, plaintext) = await SeedApiKeyAsync(f.ConnectionString, f.TenantId, f.MemberId, SendScope);
        using var keyed = CreateExternalClient(f.Factory, plaintext);
        using var invented = CreateExternalClient(f.Factory, InventedKey);

        Assert.Equal(HttpStatusCode.OK, (await PostProbeAsync(keyed)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostProbeAsync(keyed)).StatusCode); // límite de la clave
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostProbeAsync(invented)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostProbeAsync(invented)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostProbeAsync(invented)).StatusCode); // límite por IP

        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM platform.request_failures"));
    }

    [Fact]
    public async Task A403OnTheExternalApiWritesAFailureRow()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var (_, plaintext) = await SeedApiKeyAsync(f.ConnectionString, f.TenantId, f.MemberId, SendScope);
        using var client = CreateExternalClient(f.Factory, plaintext);

        var response = await PostProbeAsync(client, ProbeUrl + "/forbidden");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            1L,
            await CountAsync(
                f.ConnectionString,
                "SELECT count(*) FROM platform.request_failures WHERE status_code = 403 AND path = @path",
                ("path", ProbeUrl + "/forbidden")));
    }

    /// <summary>Monta, sobre el grupo real de la API externa, un <c>POST</c> que responde 200 y otro que lanza un 403 de scope.</summary>
    private sealed class ProbeStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            var routes = Assert.IsAssignableFrom<IEndpointRouteBuilder>(app.Properties["__EndpointRouteBuilder"]);
            var group = ExternalApiEndpoints.MapExternalGroup(routes);
            group.MapPost("/probe", () => Results.Ok());
            group.MapPost("/probe/forbidden", Forbidden);
        };

        private static IResult Forbidden() =>
            throw new RequestForbiddenException(ExternalApiErrorCodes.ScopeMissing, "Sonda.");
    }
}
