using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure;
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

    private sealed record CapturedPart(string? Name, string? FileName, string? ContentType, byte[] Bytes);

    /// <summary>Anota método, path, partes del multipart (o el cuerpo) y responde lo que se le pida.</summary>
    private sealed class ScriptedHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public string? Path { get; private set; }

        public string? MediaType { get; private set; }

        public string? Body { get; private set; }

        public List<CapturedPart> Parts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri?.AbsolutePath;
            MediaType = request.Content?.Headers.ContentType?.MediaType;
            if (request.Content is MultipartFormDataContent multipart)
            {
                foreach (var part in multipart)
                {
                    Parts.Add(new CapturedPart(
                        part.Headers.ContentDisposition?.Name?.Trim('"'),
                        part.Headers.ContentDisposition?.FileName?.Trim('"'),
                        part.Headers.ContentType?.MediaType,
                        await part.ReadAsByteArrayAsync(cancellationToken)));
                }
            }
            else if (request.Content is not null)
            {
                Body = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(status) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class NetworkDownHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("network down");
    }

    private static WhatsAppCloudClient ClientFor(HttpMessageHandler handler) =>
        new(new SingleClientFactory(handler), Options.Create(new MessagingMetaOptions()), NullLogger<WhatsAppCloudClient>.Instance);

    private static readonly byte[] Audio = [0x4F, 0x67, 0x67, 0x53, 1, 2, 3];

    [Fact]
    public async Task AVoiceNoteUploadIsMultipartWithProductTypeAndFile()
    {
        using var handler = new ScriptedHandler(HttpStatusCode.OK, """{"id":"media.123"}""");

        var result = await ClientFor(handler).UploadMediaAsync(new MessagingSender("111", "token"), Audio, "audio/ogg", TestContext.Current.CancellationToken);

        Assert.Equal(("media.123", (MessagingGraphFailure?)null), (result.Value, result.Failure));
        Assert.Equal((HttpMethod.Post, "/v24.0/111/media", "multipart/form-data"), (handler.Method, handler.Path, handler.MediaType));
        Assert.Equal(["messaging_product", "type", "file"], handler.Parts.Select(part => part.Name));
        Assert.Equal("whatsapp", Encoding.UTF8.GetString(handler.Parts[0].Bytes));
        Assert.Equal("audio/ogg", Encoding.UTF8.GetString(handler.Parts[1].Bytes));
        Assert.Equal(("voice.ogg", "audio/ogg"), (handler.Parts[2].FileName, handler.Parts[2].ContentType));
        Assert.Equal(Audio, handler.Parts[2].Bytes);
    }

    [Theory]
    [InlineData(400, """{"error":{"code":100,"fbtrace_id":"t"}}""", 400, 100, "http_400")]
    [InlineData(500, "{}", 500, null, "http_500")]
    [InlineData(200, "{}", 200, null, "no_id")]
    [InlineData(200, """{"id":""}""", 200, null, "no_id")]
    public async Task AnUploadThatMetaDoesNotConfirmIsAFailureWithItsStatusAndCode(int status, string body, int expectedStatus, int? expectedCode, string expectedReason)
    {
        using var handler = new ScriptedHandler((HttpStatusCode)status, body);

        var result = await ClientFor(handler).UploadMediaAsync(new MessagingSender("111", "token"), Audio, "audio/ogg", TestContext.Current.CancellationToken);

        Assert.Null(result.Value);
        Assert.Equal(new MessagingGraphFailure(expectedStatus, expectedCode, expectedReason), result.Failure);
    }

    // Review Focus 2: message_media.meta_media_id es varchar(64); un id más largo haría fallar el commit después de enviar.
    [Fact]
    public async Task AnUploadIdLongerThanTheColumnIsAFailure()
    {
        using var handler = new ScriptedHandler(HttpStatusCode.OK, $$"""{"id":"{{new string('9', 65)}}"}""");

        var result = await ClientFor(handler).UploadMediaAsync(new MessagingSender("111", "token"), Audio, "audio/ogg", TestContext.Current.CancellationToken);

        Assert.Equal(new MessagingGraphFailure(200, null, "invalid_id"), result.Failure);
    }

    private sealed class RecordingClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public List<string> Names { get; } = [];

        public HttpClient CreateClient(string name)
        {
            Names.Add(name);
            var client = new HttpClient(handler, disposeHandler: false);
            WhatsAppCloudClient.ConfigureClient(client);
            return client;
        }
    }

    // Una nota de varios MB por una red lenta no cabe en los 10 s del cliente compartido: sin su propio cliente, la
    // subida se corta y la nota queda Failed por Unconfirmed sin que Meta haya contestado nada.
    [Fact]
    public async Task TheUploadGoesThroughItsOwnClientAndTheSendKeepsTheSharedOne()
    {
        using var handler = new ScriptedHandler(HttpStatusCode.OK, """{"id":"media.123","messages":[{"id":"wamid.voice"}]}""");
        var factory = new RecordingClientFactory(handler);
        var client = new WhatsAppCloudClient(factory, Options.Create(new MessagingMetaOptions()), NullLogger<WhatsAppCloudClient>.Instance);

        await client.UploadMediaAsync(new MessagingSender("111", "token"), Audio, "audio/ogg", TestContext.Current.CancellationToken);
        await client.SendAudioAsync(new MessagingSender("111", "token"), new SendToUserId("CO.1"), "media.123", "qep:x", null, TestContext.Current.CancellationToken);

        Assert.Equal(["messaging.meta-graph-upload", "messaging.meta-graph"], factory.Names);
    }

    [Fact]
    public void TheRegisteredUploadClientWaitsThirtySecondsAndTheSharedOneTen()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:QepDatabase"] = "Host=localhost;Database=unused" })
            .Build();
        using var provider = new ServiceCollection().AddLogging().AddMessagingInfrastructure(configuration).BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        using var upload = factory.CreateClient("messaging.meta-graph-upload");
        using var shared = factory.CreateClient("messaging.meta-graph");

        Assert.Equal((TimeSpan.FromSeconds(30), new Uri(WhatsAppCloudClient.BaseUrl)), (upload.Timeout, upload.BaseAddress));
        Assert.Equal(TimeSpan.FromSeconds(10), shared.Timeout);
    }

    [Fact]
    public async Task AnUnreachableGraphIsAFailureWithoutStatus()
    {
        using var handler = new NetworkDownHandler();

        var result = await ClientFor(handler).UploadMediaAsync(new MessagingSender("111", "token"), Audio, "audio/ogg", TestContext.Current.CancellationToken);

        Assert.Equal(new MessagingGraphFailure(0, null, "network"), result.Failure);
    }

    [Fact]
    public async Task AVoiceNoteGoesAsAudioWithVoiceTrueByRecipient()
    {
        using var handler = new ScriptedHandler(HttpStatusCode.OK, """{"messages":[{"id":"wamid.voice"}]}""");

        var result = await ClientFor(handler).SendAudioAsync(
            new MessagingSender("111", "token"), new SendToUserId("CO.1"), "media.1", "qep:x", null, TestContext.Current.CancellationToken);

        Assert.Equal((SendOutcome.Sent, "wamid.voice"), (result.Outcome, result.Wamid));
        Assert.Equal("/v24.0/111/messages", handler.Path);
        Assert.Contains("\"type\":\"audio\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"audio\":{\"id\":\"media.1\",\"voice\":true}", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"biz_opaque_callback_data\":\"qep:x\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"recipient\":\"CO.1\"", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"to\"", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"text\"", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"context\"", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AQuotedVoiceNoteToALegacyPhoneCarriesToAndTheContext()
    {
        using var handler = new ScriptedHandler(HttpStatusCode.OK, """{"messages":[{"id":"wamid.voice"}]}""");

        await ClientFor(handler).SendAudioAsync(
            new MessagingSender("111", "token"), new SendToPhone("573001234567"), "media.1", "qep:x", "wamid.quoted", TestContext.Current.CancellationToken);

        Assert.Contains("\"to\":\"573001234567\"", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"recipient\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"context\":{\"message_id\":\"wamid.quoted\"}", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARejectedVoiceNoteKeepsTheGraphCodeLikeText()
    {
        using var handler = new ScriptedHandler(HttpStatusCode.BadRequest, """{"error":{"code":131053,"error_user_title":"Media upload error"}}""");

        var result = await ClientFor(handler).SendAudioAsync(
            new MessagingSender("111", "token"), new SendToUserId("CO.1"), "media.1", "qep:x", null, TestContext.Current.CancellationToken);

        Assert.Equal(new SendResult(SendOutcome.GraphError, null, 131053, "Media upload error"), result);
    }
}
