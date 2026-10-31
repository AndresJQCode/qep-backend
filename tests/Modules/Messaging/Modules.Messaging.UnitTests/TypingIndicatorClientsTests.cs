using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Meta;
using Modules.Messaging.Infrastructure.Options;

namespace Modules.Messaging.UnitTests;

/// <summary>Lo que viaja a Meta en cada canal: WhatsApp engancha el indicador al último entrante y Instagram lo manda a la persona.</summary>
public sealed class TypingIndicatorClientsTests
{
    private const string Wamid = "wamid.in";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        public HttpRequestMessage? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent("""{"success":true}""", Encoding.UTF8, "application/json") };
        }
    }

    private sealed class NetworkDownHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("network down");
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler, Action<HttpClient> configure) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient(handler, disposeHandler: false);
            configure(client);
            return client;
        }
    }

    private static WhatsAppCloudClient WhatsApp(HttpMessageHandler handler) =>
        new(new SingleClientFactory(handler, WhatsAppCloudClient.ConfigureClient), Options.Create(new MessagingMetaOptions()), NullLogger<WhatsAppCloudClient>.Instance);

    private static InstagramClient Instagram(HttpMessageHandler handler) =>
        new(new SingleClientFactory(handler, InstagramClient.ConfigureClient), Options.Create(new MessagingInstagramOptions()), NullLogger<InstagramClient>.Instance);

    private static readonly MessagingSender WhatsAppSender = new("111", "token");

    private static readonly InstagramSender IgSender = new("17841400000000001", "ig-token");

    [Fact]
    public async Task WhatsAppMarksTheInboundAndAsksForTheTextIndicator()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);

        Assert.True(await WhatsApp(handler).SendTypingIndicatorAsync(WhatsAppSender, Wamid, Ct));

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("/v24.0/111/messages", handler.Request.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer token", handler.Request.Headers.Authorization!.ToString());
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"messaging_product":"whatsapp","status":"read","message_id":"wamid.in","typing_indicator":{"type":"text"}}"""),
            JsonNode.Parse(handler.Body!)));
    }

    [Fact]
    public async Task AWhatsAppRejectionIsFalse()
    {
        Assert.False(await WhatsApp(new RecordingHandler(HttpStatusCode.InternalServerError)).SendTypingIndicatorAsync(WhatsAppSender, Wamid, Ct));
    }

    [Fact]
    public async Task AWhatsAppNetworkFailureIsFalse()
    {
        Assert.False(await WhatsApp(new NetworkDownHandler()).SendTypingIndicatorAsync(WhatsAppSender, Wamid, Ct));
    }

    [Fact]
    public async Task InstagramSendsTypingOnWithNothingElse()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);

        Assert.True(await Instagram(handler).SendTypingOnAsync(IgSender, "1234567890123456", Ct));

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("/v24.0/17841400000000001/messages", handler.Request.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer ig-token", handler.Request.Headers.Authorization!.ToString());
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"recipient":{"id":"1234567890123456"},"sender_action":"typing_on"}"""),
            JsonNode.Parse(handler.Body!)));
    }

    [Fact]
    public async Task AnInstagramRejectionIsFalse()
    {
        Assert.False(await Instagram(new RecordingHandler(HttpStatusCode.BadRequest)).SendTypingOnAsync(IgSender, "1234567890123456", Ct));
    }

    [Fact]
    public async Task AnInstagramNetworkFailureIsFalse()
    {
        Assert.False(await Instagram(new NetworkDownHandler()).SendTypingOnAsync(IgSender, "1234567890123456", Ct));
    }
}
