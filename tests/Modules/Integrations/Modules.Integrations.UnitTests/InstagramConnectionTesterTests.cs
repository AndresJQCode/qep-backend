using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure;
using Modules.Integrations.Infrastructure.Meta;

namespace Modules.Integrations.UnitTests;

/// <summary>Spec 2026-10-14 §6.1, probador de <c>instagram</c>: 200 → Ok y refresca usuario y nombre; 190 → token_expired;
/// otro 4xx → credentials_rejected; 5xx, timeout y red → Unreachable. Pide <c>/me</c> con el token en Bearer y nunca lo
/// registra.</summary>
public sealed class InstagramConnectionTesterTests : IDisposable
{
    private const string Token = "ig-access-token-SENTINEL-55bb";

    private readonly StubGraphHandler _graph = new();
    private readonly UnitTestLogs _logs = new();
    private readonly ServiceProvider _services;

    public InstagramConnectionTesterTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:QepDatabase"] = "Host=localhost;Database=unit;Username=x;Password=x",
                ["Meta:Instagram:GraphApiVersion"] = "v24.0",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(_logs));
        services.AddSingleton<IHostEnvironment>(new UnitTestHostEnvironment());
        services.AddIntegrationsInfrastructure(configuration);
        services.AddHttpClient(InstagramGraphClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _graph);
        _services = services.BuildServiceProvider();
    }

    public void Dispose() => _services.Dispose();

    private Task<ConnectionTestResult> TestAsync() =>
        _services.GetRequiredService<IConnectionTester>().TestAsync(
            IntegrationProviders.Instagram,
            new Dictionary<string, string> { [InstagramFieldKeys.IgUserId] = "17841400000000001" },
            new Dictionary<string, string> { [InstagramFieldKeys.AccessToken] = Token },
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task A200RefreshesTheUsernameAndTheName()
    {
        _graph.Body = """{"id":"17841400000000001","username":"origenbotanico","name":"Origen Botánico"}""";

        var result = await TestAsync();

        Assert.Equal(ConnectionTestOutcome.Ok, result.Outcome);
        Assert.Equal("origenbotanico", result.RefreshedFields![InstagramFieldKeys.Username]);
        Assert.Equal("Origen Botánico", result.RefreshedFields[InstagramFieldKeys.AccountName]);
        var request = Assert.Single(_graph.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://graph.instagram.com/v24.0/me?fields=id,username,name", request.Uri!.ToString());
        Assert.Equal($"Bearer {Token}", request.Authorization);
        Assert.DoesNotContain(Token, _logs.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A190IsTokenExpired()
    {
        _graph.Status = HttpStatusCode.BadRequest;
        _graph.Body = """{"error":{"message":"x","type":"OAuthException","code":190,"fbtrace_id":"t"}}""";

        var result = await TestAsync();

        Assert.Equal(ConnectionTestOutcome.CredentialsRejected, result.Outcome);
        Assert.Equal(ConnectionFailureCodes.TokenExpired, result.FailureCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AnotherClientErrorIsCredentialsRejected(HttpStatusCode status)
    {
        _graph.Status = status;
        _graph.Body = """{"error":{"message":"x","type":"OAuthException","code":10,"fbtrace_id":"t"}}""";

        var result = await TestAsync();

        Assert.Equal(ConnectionTestOutcome.CredentialsRejected, result.Outcome);
        Assert.Equal(ConnectionFailureCodes.CredentialsRejected, result.FailureCode);
    }

    [Fact]
    public async Task AServerErrorIsUnreachable()
    {
        _graph.Status = HttpStatusCode.InternalServerError;

        var result = await TestAsync();

        Assert.Equal(ConnectionTestOutcome.Unreachable, result.Outcome);
        Assert.Equal("http_500", result.Reason);
    }

    [Fact]
    public async Task ANetworkFailureIsUnreachable()
    {
        _graph.Throw = new HttpRequestException("down");

        var result = await TestAsync();

        Assert.Equal(ConnectionTestOutcome.Unreachable, result.Outcome);
        Assert.Equal("network", result.Reason);
    }

    [Fact]
    public async Task WithoutATokenItIsRejectedWithoutCallingMeta()
    {
        var result = await _services.GetRequiredService<IConnectionTester>().TestAsync(
            IntegrationProviders.Instagram, new Dictionary<string, string>(), new Dictionary<string, string>(), TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionTestOutcome.CredentialsRejected, result.Outcome);
        Assert.Empty(_graph.Requests);
    }
}
