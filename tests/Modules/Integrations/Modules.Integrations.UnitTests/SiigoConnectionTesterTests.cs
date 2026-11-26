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
using Modules.Integrations.Infrastructure.Siigo;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-11-18 (integración Siigo): el probador autentica con <c>POST /auth</c> (usuario, clave de acceso y
/// <c>Partner-Id</c>) y lee <c>GET /v1/document-types?type=FV</c> con el token, que es barato y no escribe nada. 401/403 en
/// <c>/auth</c> es credencial rechazada; 5xx, timeout o red, «no pude verificar». Se arma por el registro real del módulo;
/// sólo el handler primario es de mentira.
/// </summary>
public sealed class SiigoConnectionTesterTests : IDisposable
{
    private const string Username = "api@empresa.test";
    private const string AccessKey = "siigo-access-key-SENTINEL-4b2f";
    private const string Token = "siigo-token-SENTINEL-9a1c";
    private const string PartnerId = "qepTest";

    private readonly StubSiigoHandler _api = new();
    private readonly UnitTestLogs _logs = new();
    private readonly ServiceProvider _services;

    public SiigoConnectionTesterTests()
        : this(PartnerId)
    {
    }

    private SiigoConnectionTesterTests(string? partnerId)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:QepDatabase"] = "Host=localhost;Database=unit;Username=x;Password=x",
            ["Integrations:Zenvia:BaseUrl"] = "https://zenvia.test",
            ["Siigo:BaseUrl"] = "https://siigo.test",
        };
        if (partnerId is not null)
        {
            settings["Siigo:PartnerId"] = partnerId;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(_logs));
        services.AddSingleton<IHostEnvironment>(new UnitTestHostEnvironment());
        services.AddIntegrationsInfrastructure(configuration);
        services.AddHttpClient(SiigoConnectionTester.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _api);
        _services = services.BuildServiceProvider();
    }

    public void Dispose() => _services.Dispose();

    private static Task<ConnectionTestResult> TestAsync(ServiceProvider services) =>
        services.GetRequiredService<IConnectionTester>().TestAsync(
            IntegrationProviders.Siigo,
            new Dictionary<string, string> { [SiigoFieldKeys.Username] = Username },
            new Dictionary<string, string> { [SiigoFieldKeys.AccessKey] = AccessKey },
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task ValidCredentialsAuthenticateAndReadDocumentTypes()
    {
        _api.AuthBody = $$"""{"access_token":"{{Token}}","expires_in":86400,"token_type":"Bearer"}""";

        var result = await TestAsync(_services);

        Assert.Equal(ConnectionTestOutcome.Ok, result.Outcome);
        Assert.Equal(2, _api.Requests.Count);
        var auth = _api.Requests.ElementAt(0);
        Assert.Equal(HttpMethod.Post, auth.Method);
        Assert.Equal("https://siigo.test/auth", auth.Uri?.ToString());
        Assert.Equal(PartnerId, auth.Header("Partner-Id"));
        using (var body = JsonDocument.Parse(auth.Body!))
        {
            Assert.Equal(Username, body.RootElement.GetProperty("username").GetString());
            Assert.True(body.RootElement.GetProperty("access_key").GetString() == AccessKey, "The access key must travel as access_key.");
        }

        var documentTypes = _api.Requests.ElementAt(1);
        Assert.Equal(HttpMethod.Get, documentTypes.Method);
        Assert.Equal("https://siigo.test/v1/document-types?type=FV", documentTypes.Uri?.ToString());
        Assert.True(documentTypes.Header("Authorization") == $"Bearer {Token}", "The token must travel as a bearer token.");
        Assert.Equal(PartnerId, documentTypes.Header("Partner-Id"));
        Assert.Contains("qep-integrations", documentTypes.UserAgent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task RejectedCredentialsAreReportedAsRejected(HttpStatusCode status)
    {
        _api.AuthStatus = status;

        var result = await TestAsync(_services);

        Assert.Equal(ConnectionTestOutcome.CredentialsRejected, result.Outcome);
        Assert.Single(_api.Requests);
    }

    [Fact]
    public async Task AServerErrorIsUnreachable()
    {
        _api.AuthStatus = HttpStatusCode.ServiceUnavailable;

        var result = await TestAsync(_services);

        Assert.Equal(ConnectionTestOutcome.Unreachable, result.Outcome);
        Assert.Equal("http_503", result.Reason);
    }

    [Fact]
    public async Task AServerErrorOnTheDocumentTypesIsUnreachable()
    {
        _api.DocumentTypesStatus = HttpStatusCode.InternalServerError;

        var result = await TestAsync(_services);

        Assert.Equal(ConnectionTestOutcome.Unreachable, result.Outcome);
        Assert.Equal("http_500", result.Reason);
    }

    [Fact]
    public async Task AnAuthAnswerWithoutTokenIsUnreachable()
    {
        _api.AuthBody = """{"unexpected":true}""";

        var result = await TestAsync(_services);

        Assert.Equal(ConnectionTestOutcome.Unreachable, result.Outcome);
        Assert.Equal("auth_without_token", result.Reason);
        Assert.Single(_api.Requests);
    }

    [Fact]
    public async Task ATimeoutIsUnreachable()
    {
        _api.Throw = new TaskCanceledException("timed out", new TimeoutException());

        var result = await TestAsync(_services);

        Assert.Equal(ConnectionTestOutcome.Unreachable, result.Outcome);
        Assert.Equal("timeout", result.Reason);
    }

    [Fact]
    public async Task ANetworkFailureIsUnreachable()
    {
        _api.Throw = new HttpRequestException("connection refused");

        var result = await TestAsync(_services);

        Assert.Equal(ConnectionTestOutcome.Unreachable, result.Outcome);
        Assert.Equal("network", result.Reason);
    }

    // Una conexión que se cae a mitad del cuerpo de /auth (HttpIOException hereda de IOException) también es «no pude verificar».
    [Fact]
    public async Task AConnectionResetWhileReadingTheAuthBodyIsUnreachable()
    {
        _api.AuthBodyFailure = new IOException("reset");

        var result = await TestAsync(_services);

        Assert.Equal(ConnectionTestOutcome.Unreachable, result.Outcome);
        Assert.Equal("network", result.Reason);
    }

    // El handler primario es de mentira, así que esta prueba sólo dice que un 3xx es «no pude verificar» y que no hay segunda llamada;
    // que el handler real no sigue redirecciones lo prueba CompositionRootTests.TheSiigoTesterHttpClientHasNoLoggersAndNoRedirects.
    [Fact]
    public async Task A3xxAnswerIsUnreachableAndNothingElseIsCalled()
    {
        _api.AuthStatus = HttpStatusCode.Found;
        _api.Location = "https://elsewhere.test/auth";

        var result = await TestAsync(_services);

        Assert.Equal(ConnectionTestOutcome.Unreachable, result.Outcome);
        Assert.Single(_api.Requests);
    }

    [Fact]
    public async Task WithoutPartnerIdNothingIsSent()
    {
        var tester = new SiigoConnectionTesterTests(partnerId: null);
        try
        {
            var result = await TestAsync(tester._services);

            Assert.Equal(ConnectionTestOutcome.Unreachable, result.Outcome);
            Assert.Equal("partner_id_missing", result.Reason);
            Assert.Empty(tester._api.Requests);
        }
        finally
        {
            tester.Dispose();
        }
    }

    // Cubre el ILogger del probador. Que el HttpClient no tenga loggers (el que escribiría los headers, a nivel Trace) lo prueba
    // CompositionRootTests.TheSiigoTesterHttpClientHasNoLoggersAndNoRedirects: acá el handler primario es de mentira.
    [Fact]
    public async Task TheTestersOwnLogsNeverCarryTheAccessKeyNorTheToken()
    {
        _api.AuthBody = $$"""{"access_token":"{{Token}}"}""";
        _api.DocumentTypesStatus = HttpStatusCode.Unauthorized;

        await TestAsync(_services);

        Assert.DoesNotContain(AccessKey, _logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, _logs.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public void TheClientWaitsFifteenSeconds() =>
        Assert.Equal(
            TimeSpan.FromSeconds(15),
            _services.GetRequiredService<IHttpClientFactory>().CreateClient(SiigoConnectionTester.HttpClientName).Timeout);
}

internal sealed class StubSiigoHandler : HttpMessageHandler
{
    public HttpStatusCode AuthStatus { get; set; } = HttpStatusCode.OK;

    public string AuthBody { get; set; } = """{"access_token":"token","expires_in":86400,"token_type":"Bearer"}""";

    public HttpStatusCode DocumentTypesStatus { get; set; } = HttpStatusCode.OK;

    public string? Location { get; set; }

    public Exception? Throw { get; set; }

    /// <summary>Si se pone, el cuerpo de <c>/auth</c> falla al leerse con esta excepción (los headers ya llegaron).</summary>
    public Exception? AuthBodyFailure { get; set; }

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

        var isAuth = request.RequestUri?.AbsolutePath == "/auth";
        var response = isAuth
            ? new HttpResponseMessage(AuthStatus)
            {
                Content = AuthBodyFailure is { } bodyFailure ? new FailingContent(bodyFailure) : new StringContent(AuthBody),
            }
            : new HttpResponseMessage(DocumentTypesStatus) { Content = new StringContent("[]") };
        if (Location is not null)
        {
            response.Headers.Location = new Uri(Location);
        }

        return response;
    }
}

/// <summary>Un cuerpo que lanza al leerse, como una conexión que se cae a mitad de la respuesta.</summary>
internal sealed class FailingContent(Exception failure) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => Task.FromException(failure);

    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromException<Stream>(failure);

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}

internal sealed record StubSiigoRequest(
    HttpMethod Method,
    Uri? Uri,
    IReadOnlyDictionary<string, string> Headers,
    string UserAgent,
    string? Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}
