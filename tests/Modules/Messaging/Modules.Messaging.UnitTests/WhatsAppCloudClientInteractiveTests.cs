using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure;
using Modules.Messaging.Infrastructure.Meta;
using Modules.Messaging.Infrastructure.Options;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-21 D-I1: un interactivo sale por el mismo <c>MessagePayload</c> del texto; sólo cambia el <c>type</c>.</summary>
public sealed class WhatsAppCloudClientInteractiveTests
{
    private sealed class ScriptedHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public string? Path { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
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

    private static WhatsAppCloudClient ClientFor(HttpMessageHandler handler) =>
        new(new SingleClientFactory(handler), Options.Create(new MessagingMetaOptions()), NullLogger<WhatsAppCloudClient>.Instance);

    [Fact]
    public async Task PostsAnInteractivePayloadWithCallbackData()
    {
        using var handler = new ScriptedHandler(HttpStatusCode.OK, """{"messages":[{"id":"wamid.1"}]}""");

        var result = await ClientFor(handler).SendInteractiveAsync(
            new MessagingSender("111", "token"), new SendToUserId("bsuid-1"), new ButtonsSend("x", [new("a", "A")]), "qep:abc", null, TestContext.Current.CancellationToken);

        Assert.Equal(SendOutcome.Sent, result.Outcome);
        Assert.Equal("wamid.1", result.Wamid);
        Assert.EndsWith("/111/messages", handler.Path, StringComparison.Ordinal);
        var body = JsonNode.Parse(handler.Body!)!;
        Assert.Equal("interactive", (string)body["type"]!);
        Assert.Equal("button", (string)body["interactive"]!["type"]!);
        Assert.Equal("qep:abc", (string)body["biz_opaque_callback_data"]!);
        Assert.Equal("bsuid-1", (string)body["recipient"]!);
    }

    [Fact]
    public async Task AGraphErrorMapsLikeText()
    {
        using var handler = new ScriptedHandler(HttpStatusCode.BadRequest, """{"error":{"code":131047,"error_user_title":"Re-engagement message"}}""");

        var result = await ClientFor(handler).SendInteractiveAsync(
            new MessagingSender("111", "token"), new SendToUserId("bsuid-1"), new ButtonsSend("x", [new("a", "A")]), "qep:abc", null, TestContext.Current.CancellationToken);

        Assert.Equal(SendOutcome.GraphError, result.Outcome);
        Assert.Equal(131047, result.Code);
    }
}
