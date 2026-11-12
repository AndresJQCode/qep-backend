using System.Net;
using BuildingBlocks.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Store.Application;
using Modules.Store.Infrastructure.Captcha;

namespace Modules.Store.UnitTests;

/// <summary>
/// Las ramas del verificador en que el fallo es nuestro o de Cloudflare, no del visitante: un 4xx, una respuesta que no se lee, y los
/// códigos que Cloudflare entrega con HTTP 200 cuando la llave o el servicio están mal. Todos son 503, nunca «eres un bot».
/// </summary>
public sealed class CloudflareTurnstileVerifierFailureTests
{
    private const string Host = "tienda-demo.lulocrm.com";

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static CloudflareTurnstileVerifier Verifier(HttpResponseMessage response) =>
        new(
            new StubFactory(new StubHandler(response)),
            new StoreOptions { Turnstile = new TurnstileOptions { Enabled = true, SecretKey = "turnstile-test-secret" } },
            enforceHostname: true,
            NullLogger<CloudflareTurnstileVerifier>.Instance);

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    private static async Task AssertUnavailableAsync(HttpResponseMessage response)
    {
        var failure = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => Verifier(response).VerifyAsync("tok", "203.0.113.9", Host, TestContext.Current.CancellationToken));

        Assert.Equal("store.captcha.unavailable", failure.Code);
    }

    [Fact]
    public Task AClientErrorFromCloudflareIsServiceUnavailable() =>
        AssertUnavailableAsync(Json("""{"success":false}""", HttpStatusCode.BadRequest));

    [Fact]
    public Task AnUnreadableAnswerIsServiceUnavailable() =>
        AssertUnavailableAsync(Json("this is not json"));

    [Theory]
    [InlineData("[]")]
    [InlineData("\"success\"")]
    [InlineData("null")]
    [InlineData("42")]
    public Task ARootThatIsNotAnObjectIsServiceUnavailable(string body) =>
        AssertUnavailableAsync(Json(body));

    // Cloudflare contesta 200 con success:false cuando la llave o su propio servicio están mal: es un error de despliegue o de
    // Cloudflare que tiene que verse, no un bot que se rechaza en silencio.
    [Theory]
    [InlineData("invalid-input-secret")]
    [InlineData("missing-input-secret")]
    [InlineData("internal-error")]
    public Task AConfigurationOrProviderErrorCodeIsServiceUnavailable(string code) =>
        AssertUnavailableAsync(Json($$"""{"success":false,"error-codes":["{{code}}"]}"""));

    [Fact]
    public Task ABadSecretNextToABadTokenIsStillServiceUnavailable() =>
        AssertUnavailableAsync(Json("""{"success":false,"error-codes":["invalid-input-response","invalid-input-secret"]}"""));

    [Theory]
    [InlineData("invalid-input-response")]
    [InlineData("missing-input-response")]
    [InlineData("timeout-or-duplicate")]
    [InlineData("bad-request")]
    public async Task ABadTokenCodeIsStillACaptchaFailure(string code)
    {
        var verifier = Verifier(Json($$"""{"success":false,"error-codes":["{{code}}"]}"""));

        Assert.False(await verifier.VerifyAsync("tok", "203.0.113.9", Host, TestContext.Current.CancellationToken));
    }
}
