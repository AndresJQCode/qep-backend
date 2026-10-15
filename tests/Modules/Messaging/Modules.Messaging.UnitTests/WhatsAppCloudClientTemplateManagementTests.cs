// tests/Modules/Messaging/Modules.Messaging.UnitTests/WhatsAppCloudClientTemplateManagementTests.cs
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Meta;
using Modules.Messaging.Infrastructure.Options;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-14 §4.4: crear (<c>POST /{waba}/message_templates</c>), borrar (<c>DELETE … ?hsm_id=&amp;name=</c>, «no existe» = ya
/// no está), la Resumable Upload del PDF de ejemplo (dos requests, la segunda con <c>OAuth</c> y <c>file_offset: 0</c>) y el listado con
/// <c>id</c> y <c>rejected_reason</c>. Los errores de Graph conservan <c>error_user_title</c> y <c>error_user_msg</c>; nunca el token.</summary>
public sealed class WhatsAppCloudClientTemplateManagementTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seen(HttpMethod Method, Uri Uri, string? Authorization, string? FileOffset, string? ContentType, string? Body);

    private sealed class ScriptedHandler(params (HttpStatusCode Status, string Body)[] answers) : HttpMessageHandler
    {
        private int _next;

        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Seen(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("file_offset", out var offsets) ? string.Join(",", offsets) : null,
                request.Content?.Headers.ContentType?.MediaType,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            var (status, body) = answers[Math.Min(_next++, answers.Length - 1)];
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
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

    private static readonly MessagingTemplateSource Source = new("222", "tok");

    private static (WhatsAppCloudClient Client, SingleClientFactory Factory) ClientOver(HttpMessageHandler handler, string? appId = "100200300")
    {
        var factory = new SingleClientFactory(handler);
        return (new WhatsAppCloudClient(factory, Options.Create(new MessagingMetaOptions { AppId = appId }), NullLogger<WhatsAppCloudClient>.Instance), factory);
    }

    private static string MetaError(int code, string? userTitle, string? userMessage) =>
        JsonSerializer.Serialize(new
        {
            error = new { message = "(#100) Invalid parameter", type = "OAuthException", code, error_user_title = userTitle, error_user_msg = userMessage, fbtrace_id = "t" },
        });

    [Fact]
    public async Task CreatePostsThePayloadToTheWabaAndReadsIdStatusAndCategory()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, """{"id":"777","status":"PENDING","category":"MARKETING"}"""));
        var payload = new JsonObject { ["name"] = "promo", ["language"] = "es", ["category"] = "MARKETING", ["parameter_format"] = "NAMED", ["components"] = new JsonArray() };

        var result = await ClientOver(handler).Client.CreateTemplateAsync(Source, payload, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(new CreatedTemplate("777", "PENDING", "MARKETING"), result.Value);
        var request = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Post, "/v24.0/222/message_templates", "Bearer tok"), (request.Method, request.Uri.AbsolutePath, request.Authorization));
        Assert.Equal(payload.ToJsonString(), request.Body);
    }

    [Fact]
    public async Task ACreateRejectedByMetaKeepsTheUserTitleAndMessage()
    {
        var handler = new ScriptedHandler((HttpStatusCode.BadRequest, MetaError(100, "Message Template Name Already Exists", "A template with that name and language already exists.")));

        var result = await ClientOver(handler).Client.CreateTemplateAsync(Source, new JsonObject { ["name"] = "promo" }, Ct);

        Assert.False(result.Succeeded);
        Assert.Equal((400, 100, "Message Template Name Already Exists", "A template with that name and language already exists."),
            (result.Failure!.HttpStatus, result.Failure.Code, result.Failure.UserTitle, result.Failure.UserMessage));
    }

    [Fact]
    public async Task ACreateAnswerWithoutIdIsAFailure()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, """{"success":true}"""));

        var result = await ClientOver(handler).Client.CreateTemplateAsync(Source, new JsonObject { ["name"] = "promo" }, Ct);

        Assert.False(result.Succeeded);
        Assert.Equal("no_id", result.Failure!.Reason);
    }

    [Fact]
    public async Task DeleteSendsHsmIdAndNameAsQuery()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, """{"success":true}"""));

        var result = await ClientOver(handler).Client.DeleteTemplateAsync(Source, "777", "promo", Ct);

        Assert.True(result.Succeeded);
        Assert.False(result.Value!.AlreadyGone);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("/v24.0/222/message_templates", request.Uri.AbsolutePath);
        Assert.Equal("?hsm_id=777&name=promo", request.Uri.Query);
    }

    [Fact]
    public async Task DeletingATemplateMetaNoLongerHasIsAlreadyGone()
    {
        var handler = new ScriptedHandler((HttpStatusCode.BadRequest, MetaError(100, "Invalid Template", "Message template does not exist in the given language.")));

        var result = await ClientOver(handler).Client.DeleteTemplateAsync(Source, "777", "promo", Ct);

        Assert.True(result.Succeeded);
        Assert.True(result.Value!.AlreadyGone);
    }

    [Fact]
    public async Task AnyOtherDeleteErrorIsAFailure()
    {
        var handler = new ScriptedHandler((HttpStatusCode.BadRequest, MetaError(100, "Something", "Another thing.")));

        var result = await ClientOver(handler).Client.DeleteTemplateAsync(Source, "777", "promo", Ct);

        Assert.False(result.Succeeded);
        Assert.Equal(400, result.Failure!.HttpStatus);
    }

    [Fact]
    public async Task UploadDoesTwoRequestsTheSecondWithOAuthAndFileOffsetZero()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, """{"id":"upload:MTph"}"""), (HttpStatusCode.OK, """{"h":"4::aW1hZ2U"}"""));
        var (client, factory) = ClientOver(handler);
        var bytes = new byte[] { 1, 2, 3, 4, 5 };

        var result = await client.UploadTemplateExampleAsync(Source, bytes, "application/pdf", "ejemplo.pdf", Ct);

        Assert.True(result.Succeeded);
        Assert.Equal("4::aW1hZ2U", result.Value);
        Assert.Equal(2, handler.Requests.Count);
        var start = handler.Requests[0];
        Assert.Equal((HttpMethod.Post, "/v24.0/100200300/uploads", "Bearer tok"), (start.Method, start.Uri.AbsolutePath, start.Authorization));
        Assert.Equal("?file_length=5&file_type=application%2Fpdf&file_name=ejemplo.pdf", start.Uri.Query);
        var data = handler.Requests[1];
        Assert.Equal((HttpMethod.Post, "/v24.0/upload:MTph", "OAuth tok", "0", "application/pdf"), (data.Method, data.Uri.AbsolutePath, data.Authorization, data.FileOffset, data.ContentType));
        // El cuerpo binario viaja tal cual (la prueba lo lee como texto; cinco bytes).
        Assert.Equal(5, data.Body!.Length);
        Assert.All(factory.Names, name => Assert.Equal(WhatsAppCloudClient.UploadHttpClientName, name));
    }

    [Fact]
    public async Task UploadWithoutAnAppIdFailsBeforeCallingMeta()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, "{}"));

        var result = await ClientOver(handler, appId: null).Client.UploadTemplateExampleAsync(Source, new byte[] { 1 }, "application/pdf", "x.pdf", Ct);

        Assert.False(result.Succeeded);
        Assert.Equal("no_app_id", result.Failure!.Reason);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AnUploadIdWithAnUnexpectedShapeIsNotFollowed()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, """{"id":"../evil"}"""));

        var result = await ClientOver(handler).Client.UploadTemplateExampleAsync(Source, new byte[] { 1 }, "application/pdf", "x.pdf", Ct);

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_upload_id", result.Failure!.Reason);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task UploadWithoutAHandleIsAFailure()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, """{"id":"upload:MTph"}"""), (HttpStatusCode.OK, """{}"""));

        var result = await ClientOver(handler).Client.UploadTemplateExampleAsync(Source, new byte[] { 1 }, "application/pdf", "x.pdf", Ct);

        Assert.False(result.Succeeded);
        Assert.Equal("no_handle", result.Failure!.Reason);
    }

    [Fact]
    public async Task AnUploadIdWithMetasRealSigSuffixKeepsTheSigAsTheQuery()
    {
        // Meta devuelve el id como «upload:<base64>?sig=<firma>»: la firma es query de la segunda llamada, no parte de la ruta.
        var handler = new ScriptedHandler((HttpStatusCode.OK, """{"id":"upload:MTphdHRhY2htZW50OjZk?sig=ARZqkGCA_uQ"}"""), (HttpStatusCode.OK, """{"h":"4::aW1hZ2U"}"""));

        var result = await ClientOver(handler).Client.UploadTemplateExampleAsync(Source, new byte[] { 1 }, "application/pdf", "x.pdf", Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(2, handler.Requests.Count);
        var data = handler.Requests[1];
        Assert.Equal("/v24.0/upload:MTphdHRhY2htZW50OjZk", data.Uri.AbsolutePath);
        Assert.Equal("?sig=ARZqkGCA_uQ", data.Uri.Query);
    }

    [Fact]
    public async Task ListingAsksForIdAndRejectedReason()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, """{"data":[]}"""));

        await ClientOver(handler).Client.ListTemplatesAsync(Source, Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Contains("fields=id,name,language,status,category,components,parameter_format,rejected_reason", Uri.UnescapeDataString(request.Uri.Query), StringComparison.Ordinal);
    }
}
