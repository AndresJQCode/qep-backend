using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Meta;
using Modules.Messaging.Infrastructure.Options;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-29: <c>POST /{phone_number_id}/smb_app_data</c> con el <c>sync_type</c>. Un 2xx con <c>request_id</c> es
/// éxito; un error de Graph conserva su código; sin respuesta, un 5xx o un 2xx sin <c>request_id</c> no confirman nada (P-3).</summary>
public sealed class WhatsAppCloudClientSmbAppDataTests
{
    private sealed class RecordingHandler(HttpStatusCode status, string body, Exception? failure = null) : HttpMessageHandler
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

            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
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
    public async Task ItPostsTheSyncTypeAndReturnsTheRequestId()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, """{"request_id":"REQ-1"}""");

        var result = await ClientOver(handler).RequestSmbAppDataAsync(Sender, HistorySyncType.History, TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("/v24.0/111/smb_app_data", handler.Path);
        Assert.Equal("Bearer token", handler.Authorization);
        Assert.Equal("""{"messaging_product":"whatsapp","sync_type":"history"}""", handler.Body);
        Assert.True(result.Succeeded);
        Assert.Equal("REQ-1", result.Value);
    }

    [Fact]
    public async Task TheContactsSyncTypeIsSmbAppStateSync()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, """{"request_id":"REQ-2"}""");

        await ClientOver(handler).RequestSmbAppDataAsync(Sender, HistorySyncType.SmbAppStateSync, TestContext.Current.CancellationToken);

        Assert.Equal("""{"messaging_product":"whatsapp","sync_type":"smb_app_state_sync"}""", handler.Body);
    }

    [Fact]
    public async Task AGraphErrorKeepsItsCode()
    {
        using var handler = new RecordingHandler(
            HttpStatusCode.BadRequest, """{"error":{"message":"Invalid parameter","type":"OAuthException","code":100,"fbtrace_id":"T"}}""");

        var result = await ClientOver(handler).RequestSmbAppDataAsync(Sender, HistorySyncType.History, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(400, result.Failure!.HttpStatus);
        Assert.Equal(100, result.Failure.Code);
        Assert.NotEqual("unconfirmed", result.Failure.Reason);
    }

    [Fact]
    public async Task ATimeoutOrA5xxIsUnconfirmed()
    {
        using var timeout = new RecordingHandler(HttpStatusCode.OK, "{}", new TaskCanceledException("timeout"));
        using var network = new RecordingHandler(HttpStatusCode.OK, "{}", new HttpRequestException(HttpRequestError.ResponseEnded));
        using var serverError = new RecordingHandler(
            HttpStatusCode.ServiceUnavailable, """{"error":{"message":"busy","code":2,"fbtrace_id":"T"}}""");

        var timedOut = await ClientOver(timeout).RequestSmbAppDataAsync(Sender, HistorySyncType.History, TestContext.Current.CancellationToken);
        var lost = await ClientOver(network).RequestSmbAppDataAsync(Sender, HistorySyncType.History, TestContext.Current.CancellationToken);
        var failed = await ClientOver(serverError).RequestSmbAppDataAsync(Sender, HistorySyncType.History, TestContext.Current.CancellationToken);

        Assert.Equal(new MessagingGraphFailure(0, null, "unconfirmed"), timedOut.Failure);
        Assert.Equal(new MessagingGraphFailure(0, null, "unconfirmed"), lost.Failure);
        Assert.Equal("unconfirmed", failed.Failure!.Reason);
        Assert.Equal(503, failed.Failure.HttpStatus);
    }

    [Fact]
    public async Task A2xxWithoutRequestIdIsUnconfirmed()
    {
        using var empty = new RecordingHandler(HttpStatusCode.OK, "{}");
        using var unreadable = new RecordingHandler(HttpStatusCode.OK, "not json");

        var withoutId = await ClientOver(empty).RequestSmbAppDataAsync(Sender, HistorySyncType.History, TestContext.Current.CancellationToken);
        var garbled = await ClientOver(unreadable).RequestSmbAppDataAsync(Sender, HistorySyncType.History, TestContext.Current.CancellationToken);

        Assert.Equal("unconfirmed", withoutId.Failure!.Reason);
        Assert.Equal("unconfirmed", garbled.Failure!.Reason);
    }
}
