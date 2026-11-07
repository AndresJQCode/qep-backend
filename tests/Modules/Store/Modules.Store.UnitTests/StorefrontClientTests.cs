using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Store.Api;
using Modules.Store.Application;
using Modules.Store.Domain;

namespace Modules.Store.UnitTests;

/// <summary>La clave compartida del storefront y la IP del visitante. Las pruebas de integración que necesitan rutas públicas
/// (429, cubetas, cookie, IP falsificada) llegan con las rutas, en la tarea 11.</summary>
public sealed class StorefrontClientTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef";

    private static readonly string[] RotatingIps = ["1.1.1.1", "2.2.2.2", "3.3.3.3"];

    private static DefaultHttpContext Request(string? key = Secret, string? visitorIp = null, string? remoteIp = "10.0.0.9")
    {
        var context = new DefaultHttpContext();
        if (key is not null)
        {
            context.Request.Headers[StorefrontClient.KeyHeader] = key;
        }

        if (visitorIp is not null)
        {
            context.Request.Headers[StorefrontClient.VisitorIpHeader] = visitorIp;
        }

        context.Connection.RemoteIpAddress = remoteIp is null ? null : IPAddress.Parse(remoteIp);
        return context;
    }

    [Fact]
    public void AValidKeyAuthenticates() =>
        Assert.True(StorefrontClient.TryAuthenticate(Request(), Secret));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0123456789abcdef0123456789abcdeX")]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0")]
    public void AMissingOrWrongKeyDoesNotAuthenticate(string? presented) =>
        Assert.False(StorefrontClient.TryAuthenticate(Request(presented), Secret));

    [Fact]
    public void TwoKeyHeadersDoNotAuthenticate()
    {
        var context = Request();
        context.Request.Headers[StorefrontClient.KeyHeader] = new Microsoft.Extensions.Primitives.StringValues([Secret, Secret]);

        Assert.False(StorefrontClient.TryAuthenticate(context, Secret));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankSecretRejectsEveryCall(string? secret)
    {
        // Falla cerrado: ni la clave vacía ni la de solo espacios ni ninguna otra pasan contra un secreto sin configurar.
        Assert.False(StorefrontClient.TryAuthenticate(Request(""), secret));
        Assert.False(StorefrontClient.TryAuthenticate(Request("   "), secret));
        Assert.False(StorefrontClient.TryAuthenticate(Request(null), secret));
        Assert.False(StorefrontClient.TryAuthenticate(Request(Secret), secret));
    }

    [Fact]
    public void TheVisitorIpIsUsedOnlyWithAValidKey()
    {
        Assert.Equal("203.0.113.7", StorefrontClient.VisitorIp(Request(visitorIp: "203.0.113.7"), Secret));
        Assert.Equal("10.0.0.9", StorefrontClient.VisitorIp(Request(key: null, visitorIp: "203.0.113.7"), Secret));
        Assert.Equal("10.0.0.9", StorefrontClient.VisitorIp(Request(key: "wrong", visitorIp: "203.0.113.7"), Secret));
        Assert.Equal("10.0.0.9", StorefrontClient.VisitorIp(Request(visitorIp: "203.0.113.7"), ""));
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("")]
    [InlineData("1.2.3.4, 5.6.7.8")]
    [InlineData("999.1.1.1")]
    public void AnUnparseableVisitorIpFallsBackToTheRemoteIp(string declared)
    {
        var context = Request(visitorIp: declared);

        Assert.Equal("10.0.0.9", StorefrontClient.VisitorIp(context, Secret));
        Assert.False(StorefrontClient.TryGetVisitorIp(context, Secret, out _));
    }

    [Fact]
    public void WithoutAnyIpTheFallbackIsUnknown() =>
        Assert.Equal("unknown", StorefrontClient.VisitorIp(Request(remoteIp: null), Secret));

    [Fact]
    public void AnIpv6VisitorIsNormalised() =>
        Assert.Equal("2001:db8::1", StorefrontClient.VisitorIp(Request(visitorIp: " 2001:0db8:0:0:0:0:0:1 "), Secret));

    // Particiones del limitador: la cubeta es VisitorIp, así que dos visitantes tras el mismo servidor no comparten cupo.
    [Fact]
    public void TwoVisitorsBehindTheSameServerGetSeparatePartitions()
    {
        var first = StorefrontClient.VisitorIp(Request(visitorIp: "203.0.113.1"), Secret);
        var second = StorefrontClient.VisitorIp(Request(visitorIp: "203.0.113.2"), Secret);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void RotatingTheVisitorIpWithoutTheKeyStaysInOnePartition()
    {
        var partitions = RotatingIps
            .Select(ip => StorefrontClient.VisitorIp(Request(key: null, visitorIp: ip), Secret))
            .Distinct()
            .ToList();

        Assert.Equal(["10.0.0.9"], partitions);
    }

    private static async Task<(object? Result, bool NextCalled, DefaultHttpContext Context)> RunFilterAsync(
        DefaultHttpContext context, string? secret)
    {
        context.RequestServices = new ServiceCollection()
            .AddSingleton(Options.Create(new StoreOptions { Storefront = new StorefrontOptions { SharedSecret = secret } }))
            .BuildServiceProvider();
        var nextCalled = false;
        var result = await new StorefrontKeyFilter(Microsoft.Extensions.Logging.Abstractions.NullLogger<StorefrontKeyFilter>.Instance).InvokeAsync(
            new DefaultEndpointFilterInvocationContext(context),
            _ =>
            {
                nextCalled = true;
                return ValueTask.FromResult<object?>("handler");
            });
        return (result, nextCalled, context);
    }

    [Fact]
    public async Task TheFilterRejectsACallWithoutTheKey()
    {
        var (result, nextCalled, _) = await RunFilterAsync(Request(key: null), Secret);

        Assert.False(nextCalled);
        var problem = Assert.IsAssignableFrom<IResult>(result);
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        http.Response.Body = new MemoryStream();
        await problem.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        var body = await new StreamReader(http.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status401Unauthorized, http.Response.StatusCode);
        Assert.Contains(StoreErrorCodes.PublicClientUnauthorized, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFilterRejectsEveryCallWhenTheSecretIsBlank()
    {
        var (_, nextCalled, _) = await RunFilterAsync(Request(key: ""), "  ");

        Assert.False(nextCalled);
    }

    [Fact]
    public async Task TheFilterLetsAValidCallThroughAndLeavesTheVisitorIp()
    {
        var (result, nextCalled, context) = await RunFilterAsync(Request(visitorIp: "203.0.113.7"), Secret);

        Assert.True(nextCalled);
        Assert.Equal("handler", result);
        Assert.Equal("203.0.113.7", context.Items[StorefrontClient.VisitorIpItem]);
    }
}
