using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure;
using Modules.Integrations.Infrastructure.ExternalApi;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-21 D-H3: <c>GET {baseUrl}{testPath}</c> con el header de la clave; 2xx o 3xx Ok, 401/403 rechazada, otro status
/// «no pude verificar», y una URL que la política de D-H2 no acepta marca <c>baseUrl</c>. Se arma por el registro real del
/// módulo —timeout, User-Agent y sin loggers de headers—; sólo el handler primario es de mentira, como en
/// <see cref="ZenviaConnectionTesterTests"/>.
/// </summary>
public sealed class ExternalApiConnectionTesterTests : IDisposable
{
    private const string ApiKey = "external-api-key-SENTINEL-3e7b";
    private const string Body = "external-api-body-SENTINEL-8c1d";
    private const string BaseUrl = "https://api.x.co";

    private readonly StubExternalApiHandler _api = new();
    private readonly UnitTestLogs _logs = new();
    private readonly ServiceProvider _services;

    public ExternalApiConnectionTesterTests()
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
        services.AddHttpClient(ExternalApiConnectionTester.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _api);
        _services = services.BuildServiceProvider();
    }

    public void Dispose() => _services.Dispose();

    private Task<ConnectionTestResult> TestAsync(
        string baseUrl = BaseUrl, string? authHeader = null, string? testPath = null, CancellationToken? cancellationToken = null)
    {
        var fields = new Dictionary<string, string> { [ExternalApiFieldKeys.BaseUrl] = baseUrl };
        if (authHeader is not null)
        {
            fields[ExternalApiFieldKeys.AuthHeader] = authHeader;
        }

        if (testPath is not null)
        {
            fields[ExternalApiFieldKeys.TestPath] = testPath;
        }

        return _services.GetRequiredService<IConnectionTester>().TestAsync(
            IntegrationProviders.ExternalApi,
            fields,
            new Dictionary<string, string> { [ExternalApiFieldKeys.ApiKey] = ApiKey },
            cancellationToken ?? TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A200IsOkAndSendsTheHeader()
    {
        var result = await TestAsync();

        Assert.Equal(ConnectionTestOutcome.Ok, result.Outcome);
        var request = Assert.Single(_api.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.x.co/", request.Uri?.ToString());
        Assert.Equal(ApiKey, request.Header("x-api-key"));
        Assert.Contains("qep-integrations", request.UserAgent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A302IsOkWithoutFollowing()
    {
        _api.Status = HttpStatusCode.Found;
        _api.Location = "https://otro.example/login";

        var result = await TestAsync();

        Assert.Equal(ConnectionTestOutcome.Ok, result.Outcome);
        Assert.Single(_api.Requests);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task A401IsCredentialsRejected(int status)
    {
        _api.Status = (HttpStatusCode)status;

        Assert.Equal(ConnectionTestOutcome.CredentialsRejected, (await TestAsync()).Outcome);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(404)]
    [InlineData(429)]
    public async Task A500IsUnreachableWithTheStatus(int status)
    {
        _api.Status = (HttpStatusCode)status;

        var result = await TestAsync();

        Assert.Equal(ConnectionTestOutcome.Unreachable, result.Outcome);
        Assert.Equal($"http_{status}", result.Reason);
    }

    [Fact]
    public async Task ACustomHeaderAndTestPathAreUsed()
    {
        var result = await TestAsync(baseUrl: "https://api.x.co/v1/", authHeader: "X-Token", testPath: "/health");

        Assert.Equal(ConnectionTestOutcome.Ok, result.Outcome);
        var request = Assert.Single(_api.Requests);
        Assert.Equal("https://api.x.co/v1/health", request.Uri?.ToString());
        Assert.Equal(ApiKey, request.Header("X-Token"));
        Assert.Null(request.Header("x-api-key"));
    }

    [Theory]
    [InlineData("http://api.x.co", "scheme_not_https")]
    [InlineData("https://10.0.0.5", "host_is_ip_literal")]
    public async Task AnHttpBaseUrlIsInvalidOnTheField(string baseUrl, string reason)
    {
        var result = await TestAsync(baseUrl: baseUrl);

        Assert.Equal(ConnectionTestOutcome.Invalid, result.Outcome);
        Assert.Equal(ExternalApiFieldKeys.BaseUrl, result.FieldKey);
        Assert.Equal(reason, result.Reason);
        Assert.Empty(_api.Requests);
    }

    // El handler real lanza esto desde ConnectCallback cuando el nombre resuelve a un rango interno (D-H2).
    [Fact]
    public async Task AHostResolvingToAForbiddenRangeIsInvalidOnTheField()
    {
        _api.Throw = new HttpRequestException("blocked", new ExternalApiUrlRejectedException("resolved_to_forbidden_range"));

        var result = await TestAsync();

        Assert.Equal(ConnectionTestOutcome.Invalid, result.Outcome);
        Assert.Equal(ExternalApiFieldKeys.BaseUrl, result.FieldKey);
        Assert.Equal("resolved_to_forbidden_range", result.Reason);
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
    public async Task ACancellationByTheCallerPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TestAsync(cancellationToken: cancellation.Token));
    }

    [Fact]
    public void TheClientWaitsTenSeconds() =>
        Assert.Equal(
            TimeSpan.FromSeconds(10),
            _services.GetRequiredService<IHttpClientFactory>().CreateClient(ExternalApiConnectionTester.HttpClientName).Timeout);

    // D-H5: status y resultado, nunca la clave ni el cuerpo (que ni se lee).
    [Fact]
    public async Task NothingLoggedCarriesTheKeyOrTheBody()
    {
        _api.Status = HttpStatusCode.Unauthorized;
        _api.Body = $"{{\"message\":\"{Body}\",\"echo\":\"{ApiKey}\"}}";

        await TestAsync();

        Assert.Contains(_logs.Entries, entry => entry.Contains("401", StringComparison.Ordinal));
        Assert.DoesNotContain(ApiKey, _logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(Body, _logs.AllText, StringComparison.Ordinal);
    }
}

internal sealed record StubExternalApiRequest(
    HttpMethod Method, Uri? Uri, IReadOnlyDictionary<string, string> Headers, string UserAgent)
{
    public string? Header(string name) =>
        Headers.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>Una API externa de mentira: anota cada request y responde lo que la prueba pida.</summary>
internal sealed class StubExternalApiHandler : HttpMessageHandler
{
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    public string Body { get; set; } = "{}";

    public string? Location { get; set; }

    public Exception? Throw { get; set; }

    public ConcurrentQueue<StubExternalApiRequest> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Enqueue(new StubExternalApiRequest(
            request.Method,
            request.RequestUri,
            request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase),
            request.Headers.UserAgent.ToString()));
        if (Throw is { } failure)
        {
            throw failure;
        }

        var response = new HttpResponseMessage(Status) { Content = new StringContent(Body) };
        if (Location is not null)
        {
            response.Headers.Location = new Uri(Location);
        }

        return Task.FromResult(response);
    }
}
