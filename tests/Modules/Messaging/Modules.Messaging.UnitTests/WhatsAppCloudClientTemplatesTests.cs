using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Meta;
using Modules.Messaging.Infrastructure.Options;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-11 §5.1: el payload de <c>SendTemplateAsync</c> (a <c>to</c> si hay teléfono, si no a <c>recipient</c>;
/// <c>parameter_name</c> sólo en las nombradas; sin <c>components</c> si no hay parámetros) y <c>ListTemplatesAsync</c> (los campos
/// pedidos, el paginado por <c>paging.next</c> con tope de 10 páginas y sin salir del host de Graph).</summary>
public sealed class WhatsAppCloudClientTemplatesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seen(HttpMethod Method, Uri Uri, string? Authorization, string? Body);

    /// <summary>Responde por turnos lo que se le programe; anota cada request.</summary>
    private sealed class ScriptedHandler(params (HttpStatusCode Status, string Body)[] answers) : HttpMessageHandler
    {
        private int _next;

        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Seen(
                request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            var (status, body) = answers[Math.Min(_next++, answers.Length - 1)];
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

    private const string SentAnswer = """{"messaging_product":"whatsapp","messages":[{"id":"wamid.tpl"}]}""";

    private static async Task<(SendResult Result, Seen Request)> SendAsync(SendTarget target, TemplateSend template)
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, SentAnswer));
        var result = await ClientOver(handler).SendTemplateAsync(new MessagingSender("111", "tok"), target, template, "qep:x", Ct);
        return (result, Assert.Single(handler.Requests));
    }

    [Fact]
    public async Task ANamedTemplateGoesToThePhoneWithParameterNamesInOrder()
    {
        var (result, request) = await SendAsync(
            new SendToPhone("573001234567"),
            new TemplateSend("order_update", "es", Named: true, [new TemplateParameter("first_name", "Ana"), new TemplateParameter("order_id", "A-1")]));

        Assert.Equal(SendOutcome.Sent, result.Outcome);
        Assert.Equal("wamid.tpl", result.Wamid);
        Assert.Equal("/v24.0/111/messages", request.Uri.AbsolutePath);
        Assert.Equal("Bearer tok", request.Authorization);
        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.Equal("whatsapp", root.GetProperty("messaging_product").GetString());
        Assert.Equal("template", root.GetProperty("type").GetString());
        Assert.Equal("573001234567", root.GetProperty("to").GetString());
        Assert.False(root.TryGetProperty("recipient", out _));
        Assert.Equal("qep:x", root.GetProperty("biz_opaque_callback_data").GetString());
        Assert.False(root.TryGetProperty("context", out _));
        var template = root.GetProperty("template");
        Assert.Equal("order_update", template.GetProperty("name").GetString());
        Assert.Equal("es", template.GetProperty("language").GetProperty("code").GetString());
        var component = Assert.Single(template.GetProperty("components").EnumerateArray());
        Assert.Equal("body", component.GetProperty("type").GetString());
        Assert.Equal(
            ["text|Ana|first_name", "text|A-1|order_id"],
            component.GetProperty("parameters").EnumerateArray().Select(p => $"{p.GetProperty("type").GetString()}|{p.GetProperty("text").GetString()}|{p.GetProperty("parameter_name").GetString()}"));
    }

    [Fact]
    public async Task APositionalTemplateCarriesNoParameterName()
    {
        var (_, request) = await SendAsync(
            new SendToPhone("573001234567"), new TemplateSend("hola", "es_CO", Named: false, [new TemplateParameter("1", "Ana"), new TemplateParameter("2", "12")]));

        using var body = JsonDocument.Parse(request.Body!);
        var parameters = body.RootElement.GetProperty("template").GetProperty("components")[0].GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Equal(["Ana", "12"], parameters.Select(p => p.GetProperty("text").GetString()));
        Assert.All(parameters, p => Assert.False(p.TryGetProperty("parameter_name", out _)));
        Assert.Equal("es_CO", body.RootElement.GetProperty("template").GetProperty("language").GetProperty("code").GetString());
    }

    [Fact]
    public async Task AUrlButtonGoesAsItsOwnComponentWithItsIndex()
    {
        var (_, request) = await SendAsync(
            new SendToPhone("573001234567"),
            new TemplateSend("track", "es", Named: false, [new TemplateParameter("1", "Ana")], [new TemplateUrlButton(1, "A-1")]));

        using var body = JsonDocument.Parse(request.Body!);
        var components = body.RootElement.GetProperty("template").GetProperty("components").EnumerateArray().ToArray();
        Assert.Equal(2, components.Length);
        Assert.Equal("body", components[0].GetProperty("type").GetString());
        var button = components[1];
        Assert.Equal("button", button.GetProperty("type").GetString());
        Assert.Equal("url", button.GetProperty("sub_type").GetString());
        Assert.Equal("1", button.GetProperty("index").GetString());
        var parameter = Assert.Single(button.GetProperty("parameters").EnumerateArray());
        Assert.Equal("text", parameter.GetProperty("type").GetString());
        Assert.Equal("A-1", parameter.GetProperty("text").GetString());
    }

    [Fact]
    public async Task AUrlButtonWithoutBodyParametersSendsOnlyTheButton()
    {
        var (_, request) = await SendAsync(
            new SendToPhone("573001234567"), new TemplateSend("track", "es", Named: false, [], [new TemplateUrlButton(0, "A-1")]));

        using var body = JsonDocument.Parse(request.Body!);
        var component = Assert.Single(body.RootElement.GetProperty("template").GetProperty("components").EnumerateArray());
        Assert.Equal("button", component.GetProperty("type").GetString());
    }

    [Fact]
    public async Task ATemplateWithoutParametersCarriesNoComponents()
    {
        var (_, request) = await SendAsync(new SendToPhone("573001234567"), new TemplateSend("fijo", "es", Named: false, []));

        using var body = JsonDocument.Parse(request.Body!);
        Assert.False(body.RootElement.GetProperty("template").TryGetProperty("components", out _));
    }

    [Fact]
    public async Task ABsuidTargetGoesAsRecipientAndNeverAsTo()
    {
        var (_, request) = await SendAsync(new SendToUserId("CO.1349120865530274"), new TemplateSend("fijo", "es", Named: false, []));

        Assert.Contains("\"recipient\":\"CO.1349120865530274\"", request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"to\"", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void ATemplateTargetPrefersThePhoneAndFallsBackToTheBsuid()
    {
        Assert.Equal(new SendToPhone("573001234567"), SendTarget.ForTemplate("CO.1", "573001234567"));
        Assert.Equal(new SendToPhone("573001234567"), SendTarget.ForTemplate(null, "573001234567"));
        Assert.Equal(new SendToUserId("CO.1"), SendTarget.ForTemplate("CO.1", null));
        Assert.Throws<InvalidOperationException>(() => SendTarget.ForTemplate(null, null));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"message":"x","type":"OAuthException","code":132001,"fbtrace_id":"t"}}""", SendOutcome.GraphError, 132001)]
    [InlineData(HttpStatusCode.BadGateway, "{}", SendOutcome.Unconfirmed, null)]
    public async Task TheTemplateSendAnswersLikeTheTextOne(HttpStatusCode status, string answer, SendOutcome outcome, int? code)
    {
        var handler = new ScriptedHandler((status, answer));

        var result = await ClientOver(handler).SendTemplateAsync(
            new MessagingSender("111", "tok"), new SendToPhone("573001234567"), new TemplateSend("fijo", "es", Named: false, []), "qep:x", Ct);

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(code, result.Code);
    }

    private static string Page(string names, string? next = null) =>
        $$"""{"data":[{{string.Join(',', names.Split(',').Select(name => $$"""{"name":"{{name}}","language":"es","status":"APPROVED","category":"UTILITY","components":[]}"""))}}]{{(next is null ? string.Empty : $$""","paging":{"cursors":{"before":"a","after":"b"},"next":"{{next}}"}""")}}}""";

    [Fact]
    public async Task ListingAsksForTheFieldsOfTheNormalizerWithAPageOfOneHundred()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, Page("a,b")));

        var result = await ClientOver(handler).ListTemplatesAsync(new MessagingTemplateSource("222", "tok"), Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(["a", "b"], result.Value!.Templates.Select(element => element.GetProperty("name").GetString()));
        Assert.True(result.Value.Complete);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/v24.0/222/message_templates", request.Uri.AbsolutePath);
        Assert.Equal("fields=id,name,language,status,category,components,parameter_format,rejected_reason&limit=100", Uri.UnescapeDataString(request.Uri.Query.TrimStart('?')));
        Assert.Equal("Bearer tok", request.Authorization);
    }

    [Fact]
    public async Task ListingFollowsPagingNextUntilThereIsNone()
    {
        var handler = new ScriptedHandler(
            (HttpStatusCode.OK, Page("a", "https://graph.facebook.com/v24.0/222/message_templates?after=p2")),
            (HttpStatusCode.OK, Page("b", "https://graph.facebook.com/v24.0/222/message_templates?after=p3")),
            (HttpStatusCode.OK, Page("c")));

        var result = await ClientOver(handler).ListTemplatesAsync(new MessagingTemplateSource("222", "tok"), Ct);

        Assert.Equal(["a", "b", "c"], result.Value!.Templates.Select(element => element.GetProperty("name").GetString()));
        Assert.True(result.Value.Complete);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("?after=p2", handler.Requests[1].Uri.Query);
        Assert.Equal("Bearer tok", handler.Requests[1].Authorization);
    }

    // Revisión final F4: el corte por páginas devuelve lo que trajo, pero dice que el listado quedó incompleto.
    [Fact]
    public async Task ListingStopsAtTenPagesAndSaysItIsIncomplete()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, Page("x", "https://graph.facebook.com/v24.0/222/message_templates?after=more")));

        var result = await ClientOver(handler).ListTemplatesAsync(new MessagingTemplateSource("222", "tok"), Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(10, handler.Requests.Count);
        Assert.Equal(10, result.Value!.Templates.Count);
        Assert.False(result.Value.Complete);
    }

    // Revisión final F4: un paging.next que no se sigue también deja el listado incompleto.
    [Fact]
    public async Task ListingNeverFollowsANextLinkToAnotherHostWithTheTokenAndSaysItIsIncomplete()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, Page("a", "https://evil.example/steal?after=p2")));

        var result = await ClientOver(handler).ListTemplatesAsync(new MessagingTemplateSource("222", "tok"), Ct);

        Assert.Single(handler.Requests);
        Assert.Equal(["a"], result.Value!.Templates.Select(element => element.GetProperty("name").GetString()));
        Assert.False(result.Value.Complete);
    }

    [Fact]
    public async Task AFailureOnAnyPageIsTheFailureOfTheWholeListing()
    {
        var handler = new ScriptedHandler(
            (HttpStatusCode.OK, Page("a", "https://graph.facebook.com/v24.0/222/message_templates?after=p2")),
            (HttpStatusCode.BadRequest, """{"error":{"message":"x","type":"OAuthException","code":190,"fbtrace_id":"t"}}"""));

        var result = await ClientOver(handler).ListTemplatesAsync(new MessagingTemplateSource("222", "tok"), Ct);

        Assert.False(result.Succeeded);
        Assert.Null(result.Value);
        Assert.Equal(400, result.Failure!.HttpStatus);
        Assert.Equal(190, result.Failure.Code);
    }

    [Fact]
    public async Task AnUnreadableAnswerIsAFailureNotAnEmptyCatalog()
    {
        var handler = new ScriptedHandler((HttpStatusCode.OK, "no es json"));

        var result = await ClientOver(handler).ListTemplatesAsync(new MessagingTemplateSource("222", "tok"), Ct);

        Assert.False(result.Succeeded);
    }
}
