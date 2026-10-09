using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure;
using Modules.Integrations.Infrastructure.Verification;
using Modules.Integrations.Infrastructure.Zenvia;
using Modules.Tenancy.Domain;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-08, «Probar la credencial»: <c>GET {BaseUrl}/v2/templates</c> con <c>X-API-TOKEN</c>
/// (respuesta 1 del owner), 2xx Ok, 401/403 rechazado, todo lo demás «no pude verificar». Se arma por
/// el registro real del módulo —timeout, User-Agent y sin loggers de headers—; sólo el handler primario
/// es de mentira.
/// </summary>
public sealed class ZenviaConnectionTesterTests : IDisposable
{
    private const string Token = "zenvia-token-SENTINEL-4c1e";
    private const string Body = "zenvia-body-SENTINEL-9d2f";

    private readonly StubZenviaHandler _zenvia = new();
    private readonly UnitTestLogs _logs = new();
    private readonly ServiceProvider _services;

    public ZenviaConnectionTesterTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:QepDatabase"] = "Host=localhost;Database=unit;Username=x;Password=x",
                ["Integrations:Zenvia:BaseUrl"] = "https://zenvia.test",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(_logs));
        services.AddIntegrationsInfrastructure(configuration);
        services.AddHttpClient(ZenviaConnectionTester.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _zenvia);
        _services = services.BuildServiceProvider();
    }

    public void Dispose() => _services.Dispose();

    private Task<ConnectionTestResult> TestAsync(CancellationToken? cancellationToken = null) =>
        _services.GetRequiredService<IConnectionTester>().TestAsync(
            IntegrationProviders.Zenvia,
            new Dictionary<string, string> { [ZenviaFieldKeys.FromNumber] = "573001234567" },
            new Dictionary<string, string> { [ZenviaFieldKeys.ApiToken] = Token },
            cancellationToken ?? TestContext.Current.CancellationToken);

    [Fact]
    public async Task ItAsksForTheTemplatesWithTheTokenHeaderAndItsUserAgent()
    {
        var result = await TestAsync();

        Assert.Equal(ConnectionTestOutcome.Ok, result.Outcome);
        var request = Assert.Single(_zenvia.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://zenvia.test/v2/templates", request.Uri?.ToString());
        Assert.Equal(Token, request.Token);
        Assert.Contains("qep-integrations", request.UserAgent, StringComparison.Ordinal);
    }

    // Review Focus 4: lo que el spec no nombra no es Ok ni rechazo.
    [Theory]
    [InlineData(200, ConnectionTestOutcome.Ok)]
    [InlineData(204, ConnectionTestOutcome.Ok)]
    [InlineData(401, ConnectionTestOutcome.CredentialsRejected)]
    [InlineData(403, ConnectionTestOutcome.CredentialsRejected)]
    [InlineData(302, ConnectionTestOutcome.Unreachable)]
    [InlineData(400, ConnectionTestOutcome.Unreachable)]
    [InlineData(404, ConnectionTestOutcome.Unreachable)]
    [InlineData(429, ConnectionTestOutcome.Unreachable)]
    [InlineData(500, ConnectionTestOutcome.Unreachable)]
    [InlineData(503, ConnectionTestOutcome.Unreachable)]
    public async Task EveryStatusHasItsOutcome(int status, ConnectionTestOutcome expected)
    {
        _zenvia.Status = (HttpStatusCode)status;

        Assert.Equal(expected, (await TestAsync()).Outcome);
    }

    [Fact]
    public async Task ATimeoutIsUnreachable()
    {
        _zenvia.Throw = new TaskCanceledException("timed out", new TimeoutException());

        var result = await TestAsync();

        Assert.Equal(ConnectionTestOutcome.Unreachable, result.Outcome);
        Assert.Equal("timeout", result.Reason);
    }

    [Fact]
    public async Task ANetworkFailureIsUnreachable()
    {
        _zenvia.Throw = new HttpRequestException("connection refused");

        var result = await TestAsync();

        Assert.Equal(ConnectionTestOutcome.Unreachable, result.Outcome);
        Assert.Equal("network", result.Reason);
    }

    [Fact]
    public async Task ACancellationByTheCallerPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TestAsync(cancellation.Token));
    }

    [Fact]
    public void TheClientWaitsTenSeconds() =>
        Assert.Equal(
            TimeSpan.FromSeconds(10),
            _services.GetRequiredService<IHttpClientFactory>().CreateClient(ZenviaConnectionTester.HttpClientName).Timeout);

    // Control de seguridad del token: con redirección automática, un 3xx de Zenvia mandaría
    // X-API-TOKEN (que SocketsHttpHandler no quita, a diferencia de Authorization) al host de Location.
    [Fact]
    public void ThePrimaryHandlerNeverFollowsRedirects()
    {
        using var handler = ZenviaConnectionTester.CreatePrimaryHandler();

        Assert.False(handler.AllowAutoRedirect);
    }

    // Spec, «Nunca en un log»: código y status, nunca headers ni cuerpo.
    [Fact]
    public async Task NothingLoggedCarriesTheTokenOrTheBody()
    {
        _zenvia.Status = HttpStatusCode.Unauthorized;
        _zenvia.Body = $"{{\"message\":\"{Body}\",\"echo\":\"{Token}\"}}";

        await TestAsync();

        Assert.Contains(_logs.Entries, entry => entry.Contains("401", StringComparison.Ordinal));
        Assert.DoesNotContain(Token, _logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(Body, _logs.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryCatalogProviderHasATester()
    {
        var keys = _services.GetServices<IProviderConnectionTester>().Select(tester => tester.ProviderKey).ToHashSet();

        Assert.All(IntegrationProviders.All, provider => Assert.Contains(provider.Key, keys));
    }

    [Fact]
    public async Task AProviderWithoutATesterIsAProgrammingError()
    {
        var unknown = new IntegrationProvider(
            "sin-probador", "X", IntegrationCategory.Messaging, [TenantModuleKeys.Quotations],
            [new FieldDefinition("apiKey", "Clave", FieldKind.Secret, required: true, maxLength: 10, pattern: null, invalidMessage: "X")],
            maxConnections: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _services.GetRequiredService<IConnectionTester>().TestAsync(
            unknown, new Dictionary<string, string>(), new Dictionary<string, string>(), TestContext.Current.CancellationToken));
    }
}

internal sealed record StubZenviaRequest(HttpMethod Method, Uri? Uri, string? Token, string UserAgent);

/// <summary>Zenvia de mentira: anota cada request y responde lo que la prueba pida.</summary>
internal sealed class StubZenviaHandler : HttpMessageHandler
{
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    public string Body { get; set; } = "[]";

    public Exception? Throw { get; set; }

    public ConcurrentQueue<StubZenviaRequest> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Como el handler real: respeta la cancelación de quien llama.
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Enqueue(new StubZenviaRequest(
            request.Method,
            request.RequestUri,
            request.Headers.TryGetValues("X-API-TOKEN", out var tokens) ? tokens.FirstOrDefault() : null,
            request.Headers.UserAgent.ToString()));
        if (Throw is { } failure)
        {
            throw failure;
        }

        return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body) });
    }
}
