using System.Net;
using System.Text.Json.Nodes;
using Modules.Buyers.Application;
using static Modules.Buyers.IntegrationTests.BuyersApiHarness;

namespace Modules.Buyers.IntegrationTests;

/// <summary>
/// <c>POST /buyer/email/verify</c> y <c>POST /buyer/email/resend</c> de punta a punta. Necesitan Docker.
/// </summary>
public sealed class BuyerVerificationApiTests
{
    private const string Password = "una-contrasena-larga";
    private const string Verify = "buyers.email-verification-requested.v1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> RegisterAsync(BuyersApi api, string email = "ana@correo.com") =>
        api.PostAsync(
            "/register",
            new { email, password = Password, acceptsDataPolicy = true, turnstileToken = "token-ok", profile = (object?)null });

    private static async Task<string> LatestTokenAsync(BuyersApi api, string email = "ana@correo.com") =>
        (await api.OutboxPayloadsAsync(Verify)).Last(payload => (string?)payload["email"] == email)["token"]!.GetValue<string>();

    private static Task<HttpResponseMessage> VerifyAsync(BuyersApi api, string token) =>
        api.PostAsync("/email/verify", new { token });

    private static Task<HttpResponseMessage> ResendAsync(
        BuyersApi api, string email = "ana@correo.com", string turnstile = "token-ok", string? visitorIp = BuyersApi.AutoIp) =>
        api.PostAsync("/email/resend", new { email, turnstileToken = turnstile }, visitorIp: visitorIp);

    private static async Task<JsonNode> JsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;

    private static async Task AssertTokenInvalidAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("store.buyer.token_invalid", (string?)(await JsonAsync(response))["code"]);
    }

    private static Task<bool> IsVerifiedAsync(BuyersApi api, string email = "ana@correo.com") =>
        api.ScalarAsync<bool>("SELECT email_verified_at IS NOT NULL FROM buyers.accounts WHERE email = $1", email);

    private static Task<long> InsertTokenAsync(BuyersApi api, string purpose, string raw, string lifetime) =>
        api.ScalarAsync<long>(
            $"""
            WITH i AS (
              INSERT INTO buyers.email_tokens (id, account_id, purpose, token_hash, created_at, expires_at)
              SELECT gen_random_uuid(), id, '{purpose}', $1, now(), now() + interval '{lifetime}' FROM buyers.accounts RETURNING 1)
            SELECT count(*) FROM i
            """,
            BuyerTokenCodec.Hash(raw));

    [Fact]
    public async Task VerifyMarksTheAccountVerified()
    {
        await using var api = await OpenAsync();
        await RegisterAsync(api);
        Assert.False(await IsVerifiedAsync(api));

        var response = await VerifyAsync(api, await LatestTokenAsync(api));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(await IsVerifiedAsync(api));
        Assert.Equal(0, await api.ScalarAsync<long>("SELECT count(*) FROM buyers.sessions"));
    }

    [Fact]
    public async Task AReusedTokenIs422TokenInvalid()
    {
        await using var api = await OpenAsync();
        await RegisterAsync(api);
        var token = await LatestTokenAsync(api);
        Assert.Equal(HttpStatusCode.NoContent, (await VerifyAsync(api, token)).StatusCode);

        await AssertTokenInvalidAsync(await VerifyAsync(api, token));
    }

    [Fact]
    public async Task AnUnknownTokenIs422TokenInvalid()
    {
        await using var api = await OpenAsync();

        await AssertTokenInvalidAsync(await VerifyAsync(api, "no-existe"));
    }

    [Fact]
    public async Task AnExpiredTokenIs422TokenInvalid()
    {
        await using var api = await OpenAsync();
        await RegisterAsync(api);
        var token = await LatestTokenAsync(api);
        await api.ScalarAsync<long>(
            "WITH u AS (UPDATE buyers.email_tokens SET expires_at = now() - interval '1 minute' RETURNING 1) SELECT count(*) FROM u");

        await AssertTokenInvalidAsync(await VerifyAsync(api, token));
        Assert.False(await IsVerifiedAsync(api));
    }

    [Fact]
    public async Task AResetTokenDoesNotVerify()
    {
        await using var api = await OpenAsync();
        await RegisterAsync(api);
        var raw = BuyerTokenCodec.Generate();
        await InsertTokenAsync(api, "Reset", raw, "1 hour");

        await AssertTokenInvalidAsync(await VerifyAsync(api, raw));
        Assert.False(await IsVerifiedAsync(api));
    }

    [Fact]
    public async Task ABlankOrOversizedTokenIs422ValidationFailed()
    {
        await using var api = await OpenAsync();

        foreach (var token in new[] { "", new string('a', 129) })
        {
            var response = await VerifyAsync(api, token);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var body = await JsonAsync(response);
            Assert.Equal("validation.failed", (string?)body["code"]);
            Assert.NotNull(body["errors"]!["Token"]);
        }
    }

    // Dos altas concurrentes del mismo email sin verificar pueden dejar dos tokens Verify sin usar: cualquiera de los dos verifica
    // la cuenta y un mismo token no sirve dos veces.
    [Fact]
    public async Task ConsumingEitherOfTwoUnusedVerifyTokensVerifiesTheAccountAndTheSameTokenFailsTwice()
    {
        await using var api = await OpenAsync();
        await RegisterAsync(api);
        var first = await LatestTokenAsync(api);
        var second = BuyerTokenCodec.Generate();
        await InsertTokenAsync(api, "Verify", second, "24 hours");
        Assert.Equal(2, await api.ScalarAsync<long>("SELECT count(*) FROM buyers.email_tokens WHERE used_at IS NULL"));

        Assert.Equal(HttpStatusCode.NoContent, (await VerifyAsync(api, second)).StatusCode);

        Assert.True(await IsVerifiedAsync(api));
        await AssertTokenInvalidAsync(await VerifyAsync(api, second));
        // El otro token sigue siendo de un solo uso y la cuenta ya verificada se queda verificada.
        Assert.Equal(HttpStatusCode.NoContent, (await VerifyAsync(api, first)).StatusCode);
        Assert.True(await IsVerifiedAsync(api));
        await AssertTokenInvalidAsync(await VerifyAsync(api, first));
    }

    [Fact]
    public async Task ResendIssuesANewTokenAndInvalidatesTheOld()
    {
        await using var api = await OpenAsync();
        await RegisterAsync(api);
        var old = await LatestTokenAsync(api);

        var response = await ResendAsync(api);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(Ct));
        var payloads = await api.OutboxPayloadsAsync(Verify);
        Assert.Equal(2, payloads.Count);
        Assert.Equal("Tienda Demo", (string?)payloads[1]["storeName"]);
        Assert.Equal("acme.lulocrm.com", (string?)payloads[1]["storeHost"]);
        var fresh = payloads[1]["token"]!.GetValue<string>();
        Assert.NotEqual(old, fresh);
        Assert.Equal(1, await api.ScalarAsync<long>("SELECT count(*) FROM buyers.email_tokens WHERE used_at IS NULL"));

        await AssertTokenInvalidAsync(await VerifyAsync(api, old));
        Assert.Equal(HttpStatusCode.NoContent, (await VerifyAsync(api, fresh)).StatusCode);
    }

    [Fact]
    public async Task ResendNormalizesTheEmail()
    {
        await using var api = await OpenAsync();
        await RegisterAsync(api);

        var response = await ResendAsync(api, email: "  ANA@Correo.COM ");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(2, (await api.OutboxPayloadsAsync(Verify)).Count);
    }

    [Fact]
    public async Task ResendForAnUnknownEmailIs202AndQueuesNothing()
    {
        await using var api = await OpenAsync();

        var response = await ResendAsync(api, email: "nadie@correo.com");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(await api.OutboxPayloadsAsync(Verify));
        Assert.Equal(0, await api.ScalarAsync<long>("SELECT count(*) FROM buyers.email_tokens"));
    }

    [Fact]
    public async Task ResendForAVerifiedAccountIs202AndQueuesNothing()
    {
        await using var api = await OpenAsync();
        await api.RegisterVerifiedAsync();
        var before = (await api.OutboxPayloadsAsync(Verify)).Count;
        var tokensBefore = await api.ScalarAsync<long>("SELECT count(*) FROM buyers.email_tokens");

        var response = await ResendAsync(api);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(before, (await api.OutboxPayloadsAsync(Verify)).Count);
        Assert.Equal(tokensBefore, await api.ScalarAsync<long>("SELECT count(*) FROM buyers.email_tokens"));
    }

    [Fact]
    public async Task ResendWithAFailedCaptchaIs422()
    {
        await using var api = await OpenAsync();
        await RegisterAsync(api);
        api.Captcha.Passes = false;

        var response = await ResendAsync(api);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("store.buyer.captcha_invalid", (string?)(await JsonAsync(response))["code"]);
        Assert.Single(await api.OutboxPayloadsAsync(Verify));
    }

    [Fact]
    public async Task ResendWithoutTheVisitorIpIs422VisitorIpRequired()
    {
        await using var api = await OpenAsync();

        var response = await ResendAsync(api, visitorIp: null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("store.public.visitor_ip_required", (string?)(await JsonAsync(response))["code"]);
    }

    [Fact]
    public async Task ResendOnAnUnknownHostIs404()
    {
        await using var api = await OpenAsync();

        var response = await api.PostAsync(
            "/email/resend", new { email = "ana@correo.com", turnstileToken = "token-ok" }, host: FakeBuyerStorefronts.MissingHost);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ResendWithAnInvalidBodyIs422ValidationFailed()
    {
        await using var api = await OpenAsync();

        var response = await ResendAsync(api, email: "no-es-un-correo", turnstile: "");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var errors = (await JsonAsync(response))["errors"]!;
        Assert.NotNull(errors["Email"]);
        Assert.NotNull(errors["TurnstileToken"]);
    }
}
