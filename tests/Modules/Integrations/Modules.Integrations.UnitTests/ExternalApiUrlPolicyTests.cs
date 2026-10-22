using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure;
using Modules.Integrations.Infrastructure.ExternalApi;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-21 D-H2 y fallo P2-R28: <c>https</c> con nombre, nunca una IP literal, y un host que resuelva a un rango interno
/// se rechaza al conectar (no sólo al guardar), sin redirecciones ni proxy.
/// </summary>
public sealed class ExternalApiUrlPolicyTests
{
    [Theory]
    [InlineData("http://api.x.co", "scheme_not_https")]
    [InlineData("ftp://api.x.co", "scheme_not_https")]
    [InlineData("https://10.0.0.5", "host_is_ip_literal")]
    [InlineData("https://181.49.1.1", "host_is_ip_literal")]
    [InlineData("https://[::1]", "host_is_ip_literal")]
    [InlineData("https://[2606:4700::1]/v1", "host_is_ip_literal")]
    [InlineData("file:///etc/passwd", "scheme_not_https")]
    [InlineData("https://api.x.co", null)]
    [InlineData("https://api.x.co/v1/", null)]
    public void RejectClassifiesTheUrl(string url, string? reason) =>
        Assert.Equal(reason, ExternalApiUrlPolicy.Reject(new Uri(url)));

    [Fact]
    public void ARelativeUrlHasNoHost() =>
        Assert.Equal("host_missing", ExternalApiUrlPolicy.Reject(new Uri("/v1", UriKind.Relative)));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.0.9")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("192.0.0.170")]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.254")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.250")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("febf::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("64:ff9b::7f00:1")]
    [InlineData("64:ff9b::a9fe:a9fe")]
    [InlineData("64:ff9b:1::a00:1")]
    [InlineData("64:ff9b:1::7f00:1")]
    [InlineData("64:ff9b:1:ffff::a9fe:a9fe")]
    public void ForbiddenRanges(string ip) => Assert.True(ExternalApiUrlPolicy.IsForbidden(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("181.49.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("198.17.255.255")]
    [InlineData("198.20.0.1")]
    [InlineData("223.255.255.254")]
    [InlineData("2606:4700::1")]
    [InlineData("::ffff:181.49.1.1")]
    [InlineData("64:ff9b::b531:101")]
    [InlineData("64:ff9b:1::b531:101")]
    public void PublicAddressesAreAllowed(string ip) => Assert.False(ExternalApiUrlPolicy.IsForbidden(IPAddress.Parse(ip)));

    // Fallo P2-R30: la misma lista que el patrón del catálogo; el bloque HTTP (tarea 11) la vuelve a mirar antes de mandar.
    [Theory]
    [InlineData("Host", true)]
    [InlineData("HOST", true)]
    [InlineData("Connection", true)]
    [InlineData("Keep-Alive", true)]
    [InlineData("transfer-encoding", true)]
    [InlineData("te", true)]
    [InlineData("Trailer", true)]
    [InlineData("Upgrade", true)]
    [InlineData("Proxy-Authorization", true)]
    [InlineData("Content-Type", true)]
    [InlineData("Cookie", true)]
    [InlineData("User-Agent", true)]
    [InlineData("X-Token", false)]
    [InlineData("x-api-key", false)]
    [InlineData("Authorization", false)]
    [InlineData("Hosted-Key", false)]
    [InlineData("X-Content-Key", false)]
    public void ReservedHeadersAreTheSameForThePolicyAndTheCatalog(string name, bool reserved)
    {
        Assert.Equal(reserved, ExternalApiUrlPolicy.IsReservedHeader(name));
        Assert.Equal(!reserved, IntegrationProviders.ExternalApi.FindField(ExternalApiFieldKeys.AuthHeader)!.HasValidShape(name));
    }

    // Revisión de la tarea 10: todos los hosts de prueba reemplazan el handler primario; esto amarra que el registro real del
    // cliente es el de la política. Sin él, el handler por defecto sigue redirecciones con la clave, usa el proxy del sistema
    // y conecta a 10/8 o a 169.254.169.254 sin que nada se ponga rojo.
    [Fact]
    public void TheNamedClientIsBoundToThePolicyHandler()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:QepDatabase"] = "Host=localhost;Database=unit;Username=x;Password=x",
                ["Integrations:Zenvia:BaseUrl"] = "https://zenvia.test",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new UnitTestHostEnvironment());
        services.AddIntegrationsInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();

        HttpMessageHandler handler = provider.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(ExternalApiConnectionTester.HttpClientName);
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        var primary = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.NotNull(primary.ConnectCallback);
        Assert.False(primary.AllowAutoRedirect);
        Assert.False(primary.UseProxy);
    }

    // El nombre público que resuelve a una dirección interna (DNS rebinding incluido): basta una prohibida entre las
    // resueltas para no conectar a ninguna.
    [Fact]
    public async Task AHostResolvingToAPrivateRangeIsRejectedAtConnect()
    {
        var resolved = new ConcurrentQueue<string>();
        using var handler = ExternalApiUrlPolicy.CreateHandler((host, _) =>
        {
            resolved.Enqueue(host);
            return ValueTask.FromResult<IPAddress[]>([IPAddress.Parse("181.49.1.1"), IPAddress.Parse("10.0.0.1")]);
        });
        using var client = new HttpClient(handler);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync(new Uri("https://api.x.co/health"), TestContext.Current.CancellationToken));

        var rejected = Assert.IsType<ExternalApiUrlRejectedException>(failure.InnerException);
        Assert.Equal("resolved_to_forbidden_range", rejected.Reason);
        Assert.Equal(["api.x.co"], resolved);
    }

    [Fact]
    public async Task AHostThatResolvesToNothingIsANetworkFailure()
    {
        using var handler = ExternalApiUrlPolicy.CreateHandler((_, _) => ValueTask.FromResult<IPAddress[]>([]));
        using var client = new HttpClient(handler);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync(new Uri("https://api.x.co/"), TestContext.Current.CancellationToken));

        Assert.IsNotType<ExternalApiUrlRejectedException>(failure.InnerException);
    }

    // Una redirección llevaría la clave (header propio, que SocketsHttpHandler no quita) a otro host; un proxy haría que
    // ConnectCallback viera el proxy y no el destino (P2-R28).
    [Fact]
    public void TheHandlerNeverFollowsRedirectsNorUsesAProxy()
    {
        using var handler = ExternalApiUrlPolicy.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.NotNull(handler.ConnectCallback);
    }
}
