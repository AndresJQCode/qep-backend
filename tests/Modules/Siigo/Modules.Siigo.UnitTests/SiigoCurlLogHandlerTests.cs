using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Modules.Siigo.Infrastructure.Api;
using Xunit;

namespace Modules.Siigo.UnitTests;

public sealed class SiigoCurlLogHandlerTests
{
    private sealed class CapturingLogger : ILogger<SiigoCurlLogHandler>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private static async Task<(CapturingLogger Logger, HttpResponseMessage Response)> SendAsync(bool enabled, HttpStatusCode status, string reply)
    {
        var logger = new CapturingLogger();
        var handler = new SiigoCurlLogHandler(logger, enabled) { InnerHandler = new StubHandler(status, reply) };
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.siigo.com/auth")
        {
            Content = new StringContent("""{"username":"a@b.co","access_key":"SECRETO"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "TOKEN-REAL");
        request.Headers.TryAddWithoutValidation("Partner-Id", "qep");
        return (logger, await client.SendAsync(request));
    }

    [Fact]
    public async Task EnabledLogsACurlWithoutTheTokenOrTheAccessKeyAndTheReply()
    {
        var (logger, response) = await SendAsync(enabled: true, HttpStatusCode.BadRequest, """{"Errors":[{"Code":"invalid_total_payments"}]}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var log = string.Join('\n', logger.Lines);
        Assert.Contains("curl", log, StringComparison.Ordinal);
        Assert.Contains("https://api.siigo.com/auth", log, StringComparison.Ordinal);
        Assert.Contains("Partner-Id: qep", log, StringComparison.Ordinal);
        Assert.Contains("400", log, StringComparison.Ordinal);
        Assert.Contains("invalid_total_payments", log, StringComparison.Ordinal);
        Assert.DoesNotContain("TOKEN-REAL", log, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRETO", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASuccessfulCallLeavesNothingBecauseItsReplyCarriesTheToken()
    {
        var (logger, response) = await SendAsync(enabled: true, HttpStatusCode.OK, """{"access_token":"TOKEN-NUEVO"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(logger.Lines);
    }

    [Fact]
    public async Task DisabledLogsNothingAndPassesTheReplyThrough()
    {
        var (logger, response) = await SendAsync(enabled: false, HttpStatusCode.OK, "{}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(logger.Lines);
    }
}
