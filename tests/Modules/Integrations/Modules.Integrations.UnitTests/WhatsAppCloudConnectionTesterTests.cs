using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure;
using Modules.Integrations.Infrastructure.Meta;

namespace Modules.Integrations.UnitTests;

/// <summary>Spec 2026-10-09 §6.1, «Probador whatsapp-cloud»: 200 con cualquier status → Ok y refresca
/// (D-M14); 190 → token_expired; 133010 → number_unregistered; otro 4xx → credentials_rejected; 5xx,
/// timeout y red → Unreachable. Nunca registra el token.</summary>
public sealed class WhatsAppCloudConnectionTesterTests : IDisposable
{
    private const string Token = "meta-access-token-SENTINEL-77aa";

    private readonly StubGraphHandler _graph = new();
    private readonly UnitTestLogs _logs = new();
    private readonly ServiceProvider _services;

    public WhatsAppCloudConnectionTesterTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:QepDatabase"] = "Host=localhost;Database=unit;Username=x;Password=x",
                ["Meta:App:GraphApiVersion"] = "v24.0",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(_logs));
        services.AddSingleton<IHostEnvironment>(new UnitTestHostEnvironment());
        services.AddIntegrationsInfrastructure(configuration);
        services.AddHttpClient(MetaGraphClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _graph);
        _services = services.BuildServiceProvider();
    }

    public void Dispose() => _services.Dispose();

    private Task<ConnectionTestResult> TestAsync() =>
        _services.GetRequiredService<IConnectionTester>().TestAsync(
            IntegrationProviders.WhatsAppCloud,
            new Dictionary<string, string> { [WhatsAppCloudFieldKeys.PhoneNumberId] = "111" },
            new Dictionary<string, string> { [WhatsAppCloudFieldKeys.AccessToken] = Token },
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task A200RefreshesTheFieldsWhateverTheStatus()
    {
        _graph.Body = """{"display_phone_number":"+57 300 123 4567","verified_name":"Origen","quality_rating":"GREEN","status":"PENDING","id":"111"}""";

        var result = await TestAsync();

        Assert.Equal(ConnectionTestOutcome.Ok, result.Outcome);
        Assert.Equal("+57 300 123 4567", result.RefreshedFields![WhatsAppCloudFieldKeys.DisplayPhoneNumber]);
        Assert.Equal("Origen", result.RefreshedFields[WhatsAppCloudFieldKeys.VerifiedName]);
        Assert.Equal("GREEN", result.RefreshedFields[WhatsAppCloudFieldKeys.QualityRating]);
        var request = Assert.Single(_graph.Requests);
        Assert.Equal("https://graph.facebook.com/v24.0/111?fields=display_phone_number,verified_name,quality_rating,status", request.Uri?.ToString());
        Assert.Equal($"Bearer {Token}", request.Authorization);
        Assert.Contains("qep-integrations", request.UserAgent, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, _logs.AllText, StringComparison.Ordinal);
        // Control positivo: sin esto, un logger que no está enganchado vuelve vacía la ausencia del token.
        Assert.Contains("Graph phone-number answered HTTP 200", _logs.AllText, StringComparison.Ordinal);
    }

    // Una conexión sin token guardado (o uno que no descifra y llega ausente) se rechaza sin llamar a
    // Graph: antes lanzaba KeyNotFoundException y salía como 500.
    [Fact]
    public async Task WithoutAnAccessTokenItIsRejectedWithoutCallingGraph()
    {
        var result = await _services.GetRequiredService<IConnectionTester>().TestAsync(
            IntegrationProviders.WhatsAppCloud,
            new Dictionary<string, string> { [WhatsAppCloudFieldKeys.PhoneNumberId] = "111" },
            new Dictionary<string, string>(),
            TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionTestOutcome.CredentialsRejected, result.Outcome);
        Assert.Equal(ConnectionFailureCodes.CredentialsRejected, result.FailureCode);
        Assert.Empty(_graph.Requests);
    }

    // El cuerpo de Graph puede repetir el token: el ToString del record no lo imprime nunca.
    [Fact]
    public void TheResponseToStringNeverPrintsTheBody()
    {
        var response = new MetaGraphResponse(HttpStatusCode.BadRequest, $"{{\"echo\":\"{Token}\"}}", new MetaGraphError(100, null, "trace"), null);

        var text = response.ToString();

        Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
        Assert.Contains("400", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(401, 190, ConnectionTestOutcome.CredentialsRejected, "token_expired")]
    [InlineData(400, 133010, ConnectionTestOutcome.CredentialsRejected, "number_unregistered")]
    [InlineData(400, 100, ConnectionTestOutcome.CredentialsRejected, "credentials_rejected")]
    [InlineData(403, 10, ConnectionTestOutcome.CredentialsRejected, "credentials_rejected")]
    [InlineData(500, 1, ConnectionTestOutcome.Unreachable, null)]
    [InlineData(429, 4, ConnectionTestOutcome.Unreachable, null)]
    [InlineData(302, null, ConnectionTestOutcome.Unreachable, null)]
    public async Task EveryGraphAnswerHasItsOutcome(int status, int? code, ConnectionTestOutcome expected, string? failureCode)
    {
        _graph.Status = (HttpStatusCode)status;
        _graph.Body = code is null ? "" : $$$"""{"error":{"message":"x","type":"OAuthException","code":{{{code}}},"error_subcode":463,"fbtrace_id":"abc"}}""";

        var result = await TestAsync();

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(failureCode, result.FailureCode);
        Assert.DoesNotContain(Token, _logs.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATimeoutAndANetworkFailureAreUnreachable()
    {
        _graph.Throw = new TaskCanceledException("timed out", new TimeoutException());
        Assert.Equal("timeout", (await TestAsync()).Reason);
        _graph.Throw = new HttpRequestException("refused");
        Assert.Equal("network", (await TestAsync()).Reason);
    }

    [Fact]
    public void TheClientWaitsTenSecondsAndDoesNotFollowRedirects()
    {
        var client = _services.GetRequiredService<IHttpClientFactory>().CreateClient(MetaGraphClient.HttpClientName);
        Assert.Equal(TimeSpan.FromSeconds(10), client.Timeout);
        Assert.False(MetaGraphClient.CreatePrimaryHandler().AllowAutoRedirect);
    }
}

internal sealed record GraphRequest(HttpMethod Method, Uri? Uri, string? Authorization, string UserAgent, string? Body);

/// <summary>Graph de mentira para pruebas unitarias: anota cada request y responde lo que la prueba pida.</summary>
internal sealed class StubGraphHandler : HttpMessageHandler
{
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    public string Body { get; set; } = "{}";

    public Exception? Throw { get; set; }

    public ConcurrentQueue<GraphRequest> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue(new GraphRequest(
            request.Method, request.RequestUri, request.Headers.Authorization?.ToString(), request.Headers.UserAgent.ToString(), body));
        if (Throw is { } failure)
        {
            throw failure;
        }

        return new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
    }
}
