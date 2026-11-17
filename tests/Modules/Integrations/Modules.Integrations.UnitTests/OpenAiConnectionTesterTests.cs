using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure;
using Modules.Integrations.Infrastructure.OpenAi;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-29 §9.1, O-10 (P-6 para OpenAI): la clave de OpenAI se prueba con <c>GET /v1/models</c>, que no gasta tokens del tenant.
/// 2xx Ok; 401 y 403 rechazada; 429, 5xx, 3xx, timeout y red «no pude verificar». Se arma por el registro real del módulo; sólo el
/// handler primario es de mentira.
/// </summary>
public sealed class OpenAiConnectionTesterTests : IDisposable
{
    private const string ApiKey = "sk-proj-SENTINEL-7c1e";
    private const string Body = "openai-body-SENTINEL-2d8a";

    private readonly StubExternalApiHandler _api = new();
    private readonly UnitTestLogs _logs = new();
    private readonly ServiceProvider _services;

    public OpenAiConnectionTesterTests()
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
        services.AddSingleton<IHostEnvironment>(new UnitTestHostEnvironment());
        services.AddIntegrationsInfrastructure(configuration);
        services.AddHttpClient(OpenAiConnectionTester.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _api);
        _services = services.BuildServiceProvider();
    }

    public void Dispose() => _services.Dispose();

    private Task<ConnectionTestResult> TestAsync() =>
        _services.GetRequiredService<IConnectionTester>().TestAsync(
            IntegrationProviders.OpenAi,
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["apiKey"] = ApiKey },
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task A200IsOkAndListsTheModelsWithTheKeyAsBearer()
    {
        _api.Body = Body;

        var result = await TestAsync();

        Assert.Equal(ConnectionTestOutcome.Ok, result.Outcome);
        var request = Assert.Single(_api.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.openai.com/v1/models", request.Uri?.ToString());
        Assert.True(request.Header("Authorization") == $"Bearer {ApiKey}", "The key must travel as a bearer token.");
        Assert.Contains("qep-integrations", request.UserAgent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ARejectedKeyIsCredentialsRejected(HttpStatusCode status)
    {
        _api.Status = status;

        Assert.Equal(ConnectionTestOutcome.CredentialsRejected, (await TestAsync()).Outcome);
    }

    // Un 429 en /v1/models no dice si es cuota o tasa sin leer el cuerpo, y ninguno de los dos hace inválida la clave.
    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task AnUnavailableApiIsUnreachable(int status)
    {
        _api.Status = (HttpStatusCode)status;

        var result = await TestAsync();

        Assert.Equal(ConnectionTestOutcome.Unreachable, result.Outcome);
        Assert.Equal($"http_{status}", result.Reason);
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

    // Un 3xx no se sigue: la clave no viaja a otro host.
    [Fact]
    public async Task ARedirectIsNotFollowed()
    {
        _api.Status = HttpStatusCode.Found;
        _api.Location = "https://elsewhere.test/v1/models";

        var result = await TestAsync();

        Assert.Equal(ConnectionTestOutcome.Unreachable, result.Outcome);
        Assert.Single(_api.Requests);
    }

    [Fact]
    public async Task TheLogsCarryTheStatusButNeverTheKeyNorTheBody()
    {
        _api.Status = HttpStatusCode.Unauthorized;
        _api.Body = Body;

        await TestAsync();

        Assert.Contains(_logs.Entries, entry => entry.Contains("401", StringComparison.Ordinal));
        Assert.DoesNotContain(ApiKey, _logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(Body, _logs.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public void TheClientWaitsTenSeconds() =>
        Assert.Equal(
            TimeSpan.FromSeconds(10),
            _services.GetRequiredService<IHttpClientFactory>().CreateClient(OpenAiConnectionTester.HttpClientName).Timeout);
}
