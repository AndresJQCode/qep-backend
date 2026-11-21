using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure;
using Modules.Integrations.Infrastructure.Coordinadora;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-11-21 (integración Coordinadora) §5.1: el probador comprueba que el AGS responde con <c>Cotizador.departamentos</c> por
/// JSON-RPC y <b>no manda credenciales</b> (ninguna lectura barata las verifica). 2xx con <c>result</c> es Ok; un <c>error</c> de JSON-RPC,
/// 5xx, timeout o red, «no pude verificar». Se arma por el registro real del módulo; sólo el handler primario es de mentira.
/// </summary>
public sealed class CoordinadoraConnectionTesterTests : IDisposable
{
    private const string ApiKey = "coordinadora-apikey-SENTINEL-7d1e";
    private const string ApiPassword = "coordinadora-clave-SENTINEL-2c9a";
    private const string GuidesPassword = "coordinadora-guias-SENTINEL-5f4b";

    private readonly StubCoordinadoraHandler _api = new();
    private readonly UnitTestLogs _logs = new();
    private readonly ServiceProvider _services;

    public CoordinadoraConnectionTesterTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:QepDatabase"] = "Host=localhost;Database=unit;Username=x;Password=x",
                ["Integrations:Zenvia:BaseUrl"] = "https://zenvia.test",
                ["Coordinadora:AgsUrl"] = "https://ags.coordinadora.test/ags/1.5/server.php",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(_logs));
        services.AddSingleton<IHostEnvironment>(new UnitTestHostEnvironment());
        services.AddIntegrationsInfrastructure(configuration);
        services.AddHttpClient(CoordinadoraConnectionTester.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _api);
        _services = services.BuildServiceProvider();
    }

    public void Dispose() => _services.Dispose();

    private Task<ConnectionTestResult> TestAsync() =>
        _services.GetRequiredService<IConnectionTester>().TestAsync(
            IntegrationProviders.Coordinadora,
            new Dictionary<string, string>
            {
                [CoordinadoraFieldKeys.Nit] = "900123456",
                [CoordinadoraFieldKeys.ClientId] = "123",
                [CoordinadoraFieldKeys.GuidesUser] = "usuario.guias",
            },
            new Dictionary<string, string>
            {
                [CoordinadoraFieldKeys.ApiKey] = ApiKey,
                [CoordinadoraFieldKeys.ApiPassword] = ApiPassword,
                [CoordinadoraFieldKeys.GuidesPassword] = GuidesPassword,
            },
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task AnAgsThatAnswersDepartmentsIsOkAndNoCredentialTravels()
    {
        var result = await TestAsync();

        Assert.Equal(ConnectionTestOutcome.Ok, result.Outcome);
        var request = Assert.Single(_api.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://ags.coordinadora.test/ags/1.5/server.php", request.Uri?.ToString());
        Assert.Contains("qep-integrations", request.UserAgent, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("2.0", body.RootElement.GetProperty("jsonrpc").GetString());
        Assert.Equal("Cotizador.departamentos", body.RootElement.GetProperty("method").GetString());
        Assert.DoesNotContain(ApiKey, request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiPassword, request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(GuidesPassword, request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("900123456", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AJsonRpcErrorIsUnreachable()
    {
        _api.Body = """{"jsonrpc":"2.0","error":{"code":-32601,"message":"Method not found"},"id":1}""";

        var result = await TestAsync();

        Assert.Equal(ConnectionTestOutcome.Unreachable, result.Outcome);
        Assert.Equal("ags_without_result", result.Reason);
    }

    [Fact]
    public async Task ABodyThatIsNotJsonIsUnreachable()
    {
        _api.Body = "<html>maintenance</html>";

        var result = await TestAsync();

        Assert.Equal(ConnectionTestOutcome.Unreachable, result.Outcome);
        Assert.Equal("ags_without_result", result.Reason);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "http_401")]
    [InlineData(HttpStatusCode.Found, "http_302")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "http_503")]
    public async Task AnythingThatIsNot2xxIsUnreachableNeverRejected(HttpStatusCode status, string reason)
    {
        _api.Status = status;

        var result = await TestAsync();

        Assert.Equal(ConnectionTestOutcome.Unreachable, result.Outcome);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public async Task ATimeoutIsUnreachable()
    {
        _api.Throw = new TaskCanceledException("timed out", new TimeoutException());

        var result = await TestAsync();

        Assert.Equal(ConnectionTestOutcome.Unreachable, result.Outcome);
        Assert.Equal("timeout", result.Reason);
    }

    [Fact]
    public async Task ANetworkFailureIsUnreachable()
    {
        _api.Throw = new HttpRequestException("connection refused");

        var result = await TestAsync();

        Assert.Equal(ConnectionTestOutcome.Unreachable, result.Outcome);
        Assert.Equal("network", result.Reason);
    }

    [Fact]
    public async Task TheTestersOwnLogsNeverCarryTheSecrets()
    {
        _api.Status = HttpStatusCode.InternalServerError;

        await TestAsync();

        Assert.NotEmpty(_logs.Entries);
        Assert.DoesNotContain(ApiKey, _logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiPassword, _logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(GuidesPassword, _logs.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public void TheClientWaitsFifteenSeconds() =>
        Assert.Equal(
            TimeSpan.FromSeconds(15),
            _services.GetRequiredService<IHttpClientFactory>().CreateClient(CoordinadoraConnectionTester.HttpClientName).Timeout);
}

internal sealed class StubCoordinadoraHandler : HttpMessageHandler
{
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    public string Body { get; set; } = """{"jsonrpc":"2.0","result":[{"codigo":"05","nombre":"ANTIOQUIA"}],"id":1}""";

    public Exception? Throw { get; set; }

    public ConcurrentQueue<StubSiigoRequest> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue(new StubSiigoRequest(
            request.Method,
            request.RequestUri,
            request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase),
            request.Headers.UserAgent.ToString(),
            body));
        if (Throw is { } failure)
        {
            throw failure;
        }

        return new HttpResponseMessage(Status) { Content = new StringContent(Body) };
    }
}
