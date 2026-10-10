using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Meta;
using Modules.Messaging.Infrastructure.Options;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-10 §6.1.4: <c>recipient</c> con BSUID o <c>to</c> con teléfono, nunca los dos (si van los
/// dos, Meta usa <c>to</c>, §3); con cita, <c>context.message_id</c>.</summary>
public sealed class WhatsAppCloudClientTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"messages":[{"id":"wamid.out"}]}""", Encoding.UTF8, "application/json") };
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

    private static async Task<string> BodyOfAsync(SendTarget target, string? contextWamid)
    {
        using var handler = new CapturingHandler();
        var client = new WhatsAppCloudClient(new SingleClientFactory(handler), Options.Create(new MessagingMetaOptions()), NullLogger<WhatsAppCloudClient>.Instance);

        var result = await client.SendTextAsync(new MessagingSender("111", "token"), target, "hola", "qep:x", contextWamid, TestContext.Current.CancellationToken);

        Assert.Equal(SendOutcome.Sent, result.Outcome);
        return handler.Body!;
    }

    [Fact]
    public async Task ABsuidGoesAsRecipientAndNeverAsTo()
    {
        var body = await BodyOfAsync(new SendToUserId("CO.1349120865530274"), null);

        Assert.Contains("\"recipient\":\"CO.1349120865530274\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"to\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"context\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALegacyPhoneGoesAsToAndNeverAsRecipient()
    {
        var body = await BodyOfAsync(new SendToPhone("573001234567"), null);

        Assert.Contains("\"to\":\"573001234567\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"recipient\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AQuotedReplyCarriesTheContext() =>
        Assert.Contains("\"context\":{\"message_id\":\"wamid.quoted\"}", await BodyOfAsync(new SendToUserId("CO.1"), "wamid.quoted"), StringComparison.Ordinal);

    [Fact]
    public void TheTargetPrefersTheBsuid()
    {
        Assert.Equal(new SendToUserId("CO.1"), SendTarget.For("CO.1", "573001234567"));
        Assert.Equal(new SendToPhone("573001234567"), SendTarget.For(null, "573001234567"));
        Assert.Throws<InvalidOperationException>(() => SendTarget.For(null, null));
    }
}
