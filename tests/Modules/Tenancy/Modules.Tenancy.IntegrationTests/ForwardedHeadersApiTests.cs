using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// La IP del cliente detrás del ingress. En producción el par directo del pod es el nodo que corre
/// ingress-nginx (hostNetwork), no el cliente; sin procesar los encabezados reenviados, la política
/// <c>public</c> del rate limiter metía a todo internet en el bucket de uno o dos nodos, y
/// <c>identity.sessions.ip_address</c> guardaba la IP del nodo.
/// </summary>
/// <remarks>
/// <para>
/// La IP del cliente sale de <c>X-Real-IP</c>, no de <c>X-Forwarded-For</c>. Detrás de Cloudflare,
/// nginx manda <c>X-Forwarded-For: &lt;lo que llegó&gt;, &lt;IP del borde de Cloudflare&gt;</c>, así que
/// la entrada de más a la derecha es el borde y no el cliente; <c>X-Real-IP</c> es el
/// <c>$remote_addr</c> de nginx, que resuelve <c>CF-Connecting-IP</c> sólo si el par es de
/// Cloudflare. Por eso estas pruebas mandan los dos encabezados con valores distintos: el bucket lo
/// tiene que elegir <c>X-Real-IP</c>.
/// </para>
/// <para>
/// El par directo se fija con <see cref="Microsoft.AspNetCore.TestHost.TestServer.SendAsync"/>:
/// TestServer deja <c>RemoteIpAddress</c> en <c>null</c>, y el middleware de encabezados reenviados
/// sólo confía en el encabezado si ese par cae en una red de confianza. Se mide por el rate limiter
/// porque es el efecto que importa: la partición es <c>RemoteIpAddress</c>, así que dos clientes en
/// buckets distintos prueban que el valor cambió, y en cuál.
/// </para>
/// </remarks>
public sealed class ForwardedHeadersApiTests
{
    // Mismo valor que la ventana fija de Program.cs: se agota con exactamente estas respuestas 200.
    private const int PublicPermitLimit = 120;

    private const string TrustedNetwork = "10.50.0.0/24";

    private static readonly IPAddress IngressNode = IPAddress.Parse("10.50.0.21");
    private static readonly IPAddress UntrustedPeer = IPAddress.Parse("192.0.2.50");

    private const string ClientA = "203.0.113.10";
    private const string ClientB = "203.0.113.20";
    private const string CloudflareEdge = "198.18.0.1";

    // El caso de producción: cada cliente detrás del ingress tiene su propio bucket, aunque la
    // entrada de más a la derecha de X-Forwarded-For —el borde de Cloudflare— sea la misma para los
    // dos.
    [Fact]
    public async Task BehindATrustedProxyEachRealIpGetsItsOwnBucket()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new ForwardedHeadersApiFactory(database.GetConnectionString(), TrustedNetwork);

        await ExhaustAsync(factory, IngressNode, ClientA, _ => $"{ClientA}, {CloudflareEdge}");

        Assert.Equal(
            StatusCodes.Status429TooManyRequests,
            await GetOpenApiAsync(factory, IngressNode, ClientA, $"{ClientA}, {CloudflareEdge}"));
        Assert.Equal(
            StatusCodes.Status200OK,
            await GetOpenApiAsync(factory, IngressNode, ClientB, $"{ClientB}, {CloudflareEdge}"));
    }

    // X-Forwarded-For lo puede escribir el cliente: rotarlo no le da un bucket nuevo, y poner ahí la
    // IP de otro no lo mete en el bucket ajeno.
    [Fact]
    public async Task BehindATrustedProxyTheForwardedForHeaderIsIgnored()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new ForwardedHeadersApiFactory(database.GetConnectionString(), TrustedNetwork);

        await ExhaustAsync(factory, IngressNode, ClientA, i => $"198.51.100.{i + 1}");

        Assert.Equal(
            StatusCodes.Status429TooManyRequests,
            await GetOpenApiAsync(factory, IngressNode, ClientA, "198.51.100.250"));
        Assert.Equal(
            StatusCodes.Status200OK,
            await GetOpenApiAsync(factory, IngressNode, ClientB, ClientA));
    }

    // Un par fuera de las redes de confianza no puede elegir su bucket mandando X-Real-IP.
    [Fact]
    public async Task FromAnUntrustedPeerTheRealIpHeaderIsIgnored()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new ForwardedHeadersApiFactory(database.GetConnectionString(), TrustedNetwork);

        await ExhaustAsync(factory, UntrustedPeer, ClientA, _ => ClientA);

        Assert.Equal(
            StatusCodes.Status429TooManyRequests,
            await GetOpenApiAsync(factory, UntrustedPeer, ClientB, ClientB));
    }

    // Sin la clave configurada no se confía en nada más que en el loopback del framework: el
    // desarrollo local y las demás pruebas quedan como estaban.
    [Fact]
    public async Task WithoutConfiguredNetworksTheIngressNodeIsNotTrusted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new ForwardedHeadersApiFactory(database.GetConnectionString());

        await ExhaustAsync(factory, IngressNode, ClientA, _ => ClientA);

        Assert.Equal(
            StatusCodes.Status429TooManyRequests,
            await GetOpenApiAsync(factory, IngressNode, ClientB, ClientB));
    }

    // Una red mal escrita tumba el arranque: ignorarla dejaría todo internet en el bucket del nodo.
    [Fact]
    public async Task AnInvalidKnownNetworkStopsTheStartup()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new ForwardedHeadersApiFactory(
            database.GetConnectionString(), TrustedNetwork, "no-es-una-red");

        var exception = Assert.ThrowsAny<Exception>(() => factory.Server);

        Assert.Contains("ForwardedHeaders:KnownNetworks:1", exception.ToString(), StringComparison.Ordinal);
    }

    private static async Task ExhaustAsync(
        ForwardedHeadersApiFactory factory,
        IPAddress peer,
        string realIp,
        Func<int, string> forwardedFor)
    {
        for (var i = 0; i < PublicPermitLimit; i++)
        {
            Assert.Equal(
                StatusCodes.Status200OK,
                await GetOpenApiAsync(factory, peer, realIp, forwardedFor(i)));
        }
    }

    private static async Task<int> GetOpenApiAsync(
        ForwardedHeadersApiFactory factory, IPAddress peer, string realIp, string forwardedFor)
    {
        var context = await factory.Server.SendAsync(
            http =>
            {
                http.Request.Method = HttpMethods.Get;
                http.Request.Path = "/openapi/v1.json";
                http.Request.Headers["X-Real-IP"] = realIp;
                http.Request.Headers["X-Forwarded-For"] = forwardedFor;
                http.Request.Headers["X-Forwarded-Proto"] = "https";
                http.Connection.RemoteIpAddress = peer;
            },
            TestContext.Current.CancellationToken);
        return context.Response.StatusCode;
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

    private sealed class ForwardedHeadersApiFactory(string connectionString, params string[] knownNetworks)
        : WebApplicationFactory<Program>
    {
        private readonly string[] _knownNetworks = knownNetworks;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Fijado, nunca heredado: mismo criterio que TenantSettingsApiTests (SDD-CT-17).
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            for (var i = 0; i < _knownNetworks.Length; i++)
            {
                builder.UseSetting($"ForwardedHeaders:KnownNetworks:{i}", _knownNetworks[i]);
            }
        }
    }
}
