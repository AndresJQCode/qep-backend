using System.Net;
using BuildingBlocks.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Store.Application;
using Modules.Store.Infrastructure.Captcha;

namespace Modules.Store.UnitTests;

public sealed class CloudflareTurnstileVerifierTests
{
    private const string Host = "tienda-demo.lulocrm.com";

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(Uri? Uri, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.RequestUri, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return await respond(request);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    private static CloudflareTurnstileVerifier Verifier(StubHandler handler, bool enforceHostname = true, string? secret = "turnstile-test-secret") =>
        new(
            new StubFactory(handler),
            new StoreOptions { Turnstile = new TurnstileOptions { Enabled = true, SecretKey = secret } },
            enforceHostname,
            NullLogger<CloudflareTurnstileVerifier>.Instance);

    [Fact]
    public async Task ASuccessForTheStoreHostnameIsAccepted()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json($$"""{"success":true,"hostname":"{{Host}}"}""")));

        Assert.True(await Verifier(handler).VerifyAsync("tok", "203.0.113.9", Host, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ASuccessForAnotherHostnameIsRejected()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json("""{"success":true,"hostname":"evil.example"}""")));

        Assert.False(await Verifier(handler).VerifyAsync("tok", "203.0.113.9", Host, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheHostnameIsNotCheckedWhenNotEnforced()
    {
        // Fuera de Production Cloudflare responde con el hostname de sus llaves de prueba, que nunca es el de la tienda.
        var handler = new StubHandler(_ => Task.FromResult(Json("""{"success":true,"hostname":"example.com"}""")));

        Assert.True(await Verifier(handler, enforceHostname: false).VerifyAsync("tok", "203.0.113.9", Host, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AFailedChallengeIsRejected()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json("""{"success":false,"error-codes":["invalid-input-response"]}""")));

        Assert.False(await Verifier(handler).VerifyAsync("tok", "203.0.113.9", Host, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ATimeoutIsServiceUnavailable()
    {
        var handler = new StubHandler(_ => throw new TaskCanceledException("The request timed out."));

        var failure = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => Verifier(handler).VerifyAsync("tok", "203.0.113.9", Host, TestContext.Current.CancellationToken));

        Assert.Equal("store.captcha.unavailable", failure.Code);
    }

    [Fact]
    public async Task ANetworkErrorIsServiceUnavailable()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));

        var failure = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => Verifier(handler).VerifyAsync("tok", "203.0.113.9", Host, TestContext.Current.CancellationToken));

        Assert.Equal("store.captcha.unavailable", failure.Code);
    }

    [Fact]
    public async Task AServerErrorIsServiceUnavailable()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json("oops", HttpStatusCode.BadGateway)));

        var failure = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => Verifier(handler).VerifyAsync("tok", "203.0.113.9", Host, TestContext.Current.CancellationToken));

        Assert.Equal("store.captcha.unavailable", failure.Code);
    }

    [Fact]
    public async Task TheCallersCancellationIsNotAnUnavailableCaptcha()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var handler = new StubHandler(_ => throw new TaskCanceledException("cancelled", null, cancelled.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Verifier(handler).VerifyAsync("tok", "203.0.113.9", Host, cancelled.Token));
    }

    [Fact]
    public async Task TheSecretAndRemoteIpAreSentAsForm()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json($$"""{"success":true,"hostname":"{{Host}}"}""")));

        await Verifier(handler).VerifyAsync("the-token", "203.0.113.9", Host, TestContext.Current.CancellationToken);

        var call = Assert.Single(handler.Calls);
        Assert.Equal("https://challenges.cloudflare.com/turnstile/v0/siteverify", call.Uri?.ToString());
        var form = call.Body.Split('&').Select(pair => pair.Split('=')).ToDictionary(pair => pair[0], pair => pair[1]);
        Assert.Equal("turnstile-test-secret", form["secret"]);
        Assert.Equal("the-token", form["response"]);
        Assert.Equal("203.0.113.9", form["remoteip"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WithoutAKeyTheVerifierFailsClosedWithoutCallingCloudflare(string? secret)
    {
        var handler = new StubHandler(_ => Task.FromResult(Json("""{"success":true}""")));

        var failure = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => Verifier(handler, secret: secret).VerifyAsync("tok", "203.0.113.9", Host, TestContext.Current.CancellationToken));

        Assert.Equal("store.captcha.unavailable", failure.Code);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task ABlankTokenIsRejectedWithoutCallingCloudflare()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json("""{"success":true}""")));

        Assert.False(await Verifier(handler).VerifyAsync("  ", "203.0.113.9", Host, TestContext.Current.CancellationToken));
        Assert.Empty(handler.Calls);
    }
}
