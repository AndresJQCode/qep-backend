using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Meta;
using Modules.Messaging.Infrastructure.Options;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-26 §2: <c>POST/DELETE /{phone_number_id}/block_users</c> con el número en E.164 (el <c>+</c> lo pone el
/// cliente) y la lectura de <c>added_users</c>, <c>removed_users</c> y <c>failed_users</c>.</summary>
public sealed class WhatsAppCloudClientBlockUsersTests
{
    private const string WaId = "573001234567";

    private sealed class RecordingHandler(HttpStatusCode status, string body, string? traceHeader = null, Exception? failure = null) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public string? Path { get; private set; }

        public string? Authorization { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri?.AbsolutePath;
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (failure is not null)
            {
                throw failure;
            }

            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (traceHeader is not null)
            {
                response.Headers.TryAddWithoutValidation("x-fb-trace-id", traceHeader);
            }

            return response;
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient(handler, disposeHandler: false);
            WhatsAppCloudClient.ConfigureClient(client);
            return client;
        }
    }

    private static WhatsAppCloudClient ClientOver(HttpMessageHandler handler) =>
        new(new SingleClientFactory(handler), Options.Create(new MessagingMetaOptions()), NullLogger<WhatsAppCloudClient>.Instance);

    private static readonly MessagingSender Sender = new("111", "token");

    [Fact]
    public async Task BlockPostsThePlusPrefixedNumberToBlockUsers()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "{}");

        await ClientOver(handler).BlockUserAsync(Sender, WaId, TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("/v24.0/111/block_users", handler.Path);
        Assert.Equal("Bearer token", handler.Authorization);
        Assert.Contains("\"messaging_product\":\"whatsapp\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"block_users\":[{\"user\":\"+573001234567\"}]", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnblockSendsTheSameBodyWithDelete()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "{}");

        await ClientOver(handler).UnblockUserAsync(Sender, WaId, TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Delete, handler.Method);
        Assert.Equal("/v24.0/111/block_users", handler.Path);
        Assert.Contains("\"messaging_product\":\"whatsapp\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"block_users\":[{\"user\":\"+573001234567\"}]", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAddedUserIsApplied()
    {
        using var handler = new RecordingHandler(
            HttpStatusCode.OK, """{"block_users":{"added_users":[{"input":"+573001234567","wa_id":"573001234567"}]}}""");

        var result = await ClientOver(handler).BlockUserAsync(Sender, WaId, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(result.Value!.Applied);
        Assert.Null(result.Value.FailedCode);
    }

    [Fact]
    public async Task ARemovedUserIsApplied()
    {
        using var handler = new RecordingHandler(
            HttpStatusCode.OK, """{"block_users":{"removed_users":[{"input":"+573001234567","wa_id":"573001234567"}]}}""");

        var result = await ClientOver(handler).UnblockUserAsync(Sender, WaId, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(result.Value!.Applied);
        Assert.Null(result.Value.FailedCode);
    }

    [Fact]
    public async Task AFailedUserCarriesItsCode()
    {
        using var handler = new RecordingHandler(
            HttpStatusCode.OK,
            """{"block_users":{"failed_users":[{"input":"+573001234567","errors":[{"code":139100,"error_subcode":99}]}]}}""",
            traceHeader: "trace-header");

        var result = await ClientOver(handler).BlockUserAsync(Sender, WaId, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.False(result.Value!.Applied);
        Assert.Equal(139100, result.Value.FailedCode);
        Assert.Equal(99, result.Value.FailedSubcode);
        Assert.Equal("trace-header", result.Value.FbTraceId);
    }

    [Fact]
    public async Task ASuccessWithoutTheUserIsNotApplied()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "{}");

        var result = await ClientOver(handler).BlockUserAsync(Sender, WaId, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.False(result.Value!.Applied);
        Assert.Null(result.Value.FailedCode);
        Assert.Null(result.Value.FbTraceId);
    }

    [Fact]
    public async Task AnUnreadableSuccessIsAFailure()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "not json");

        var result = await ClientOver(handler).BlockUserAsync(Sender, WaId, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(200, result.Failure!.HttpStatus);
        Assert.Equal("unreadable", result.Failure.Reason);
    }

    [Fact]
    public async Task AGraphErrorKeepsStatusCodeSubcodeAndTrace()
    {
        using var handler = new RecordingHandler(
            HttpStatusCode.BadRequest,
            """{"error":{"message":"x","type":"OAuthException","code":131047,"error_subcode":2494,"fbtrace_id":"trace-body"}}""");

        var result = await ClientOver(handler).BlockUserAsync(Sender, WaId, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(400, result.Failure!.HttpStatus);
        Assert.Equal(131047, result.Failure.Code);
        Assert.Equal(2494, result.Failure.Subcode);
        Assert.Equal("trace-body", result.Failure.FbTraceId);
    }

    [Fact]
    public async Task AnUnreachableGraphIsStatusZero()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "{}", failure: new HttpRequestException("down"));

        var result = await ClientOver(handler).UnblockUserAsync(Sender, WaId, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.Failure!.HttpStatus);
    }
}
