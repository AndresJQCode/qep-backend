using System.Net;
using System.Text.Json.Nodes;
using static Modules.Buyers.IntegrationTests.BuyersApiHarness;

namespace Modules.Buyers.IntegrationTests;

/// <summary>
/// <c>POST /buyer/password/forgot</c> y <c>POST /buyer/password/reset</c> de punta a punta. Necesitan Docker.
/// </summary>
public sealed class BuyerPasswordRecoveryApiTests
{
    private const string Email = "ana@correo.com";
    private const string OldPassword = "una-contrasena-larga";
    private const string NewPassword = "otra-contrasena-nueva";
    private const string Reset = "buyers.password-reset-requested.v1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> ForgotAsync(
        BuyersApi api, string email = Email, string turnstile = "token-ok", string? visitorIp = BuyersApi.AutoIp) =>
        api.PostAsync("/password/forgot", new { email, turnstileToken = turnstile }, visitorIp: visitorIp);

    private static Task<HttpResponseMessage> ResetAsync(BuyersApi api, string token, string newPassword = NewPassword) =>
        api.PostAsync("/password/reset", new { token, newPassword });

    private static Task<HttpResponseMessage> RegisterUnverifiedAsync(BuyersApi api) =>
        api.PostAsync(
            "/register",
            new { email = Email, password = OldPassword, acceptsDataPolicy = true, turnstileToken = "token-ok", profile = (object?)null });

    private static async Task<JsonNode> JsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;

    private static async Task<string> LatestResetTokenAsync(BuyersApi api, string email = Email) =>
        (await api.OutboxPayloadsAsync(Reset)).Last(payload => (string?)payload["email"] == email)["token"]!.GetValue<string>();

    private static async Task AssertTokenInvalidAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("store.buyer.token_invalid", (string?)(await JsonAsync(response))["code"]);
    }

    private static async Task<string> LoginTokenAsync(BuyersApi api, string password = OldPassword)
    {
        var response = await api.PostAsync("/session", new { email = Email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (string)(await JsonAsync(response))["sessionToken"]!;
    }

    [Fact]
    public async Task ForgotForAnUnknownEmailIs202AndQueuesNothing()
    {
        await using var api = await OpenAsync();

        var response = await ForgotAsync(api, email: "nadie@correo.com");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(Ct));
        Assert.Empty(await api.OutboxPayloadsAsync(Reset));
        Assert.Equal(0, await api.ScalarAsync<long>("SELECT count(*) FROM buyers.email_tokens"));
    }

    [Fact]
    public async Task ForgotQueuesAResetEmailForTheHost()
    {
        await using var api = await OpenAsync();
        await api.RegisterVerifiedAsync();

        var response = await ForgotAsync(api);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var payload = Assert.Single(await api.OutboxPayloadsAsync(Reset));
        Assert.Equal("acme.lulocrm.com", (string?)payload["storeHost"]);
        Assert.Equal("Tienda Demo", (string?)payload["storeName"]);
        Assert.Equal(Email, (string?)payload["email"]);
        Assert.False(string.IsNullOrEmpty((string?)payload["token"]));
    }

    [Fact]
    public async Task ForgotForAnUnverifiedAccountAlsoQueuesAResetEmail()
    {
        await using var api = await OpenAsync();
        (await RegisterUnverifiedAsync(api)).EnsureSuccessStatusCode();

        var response = await ForgotAsync(api);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Single(await api.OutboxPayloadsAsync(Reset));
    }

    [Fact]
    public async Task AnotherForgotInvalidatesThePreviousResetToken()
    {
        await using var api = await OpenAsync();
        await api.RegisterVerifiedAsync();
        await ForgotAsync(api);
        var old = await LatestResetTokenAsync(api);
        await ForgotAsync(api);
        var fresh = await LatestResetTokenAsync(api);

        await AssertTokenInvalidAsync(await ResetAsync(api, old));
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(api, fresh)).StatusCode);
    }

    [Fact]
    public async Task ResetChangesThePasswordAndRevokesEverySession()
    {
        await using var api = await OpenAsync();
        await api.RegisterVerifiedAsync();
        var first = await LoginTokenAsync(api);
        var second = await LoginTokenAsync(api);
        await ForgotAsync(api);

        var response = await ResetAsync(api, await LatestResetTokenAsync(api));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        foreach (var token in new[] { first, second })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await api.SendAsync(HttpMethod.Get, "/session", token)).StatusCode);
        }

        Assert.Equal(2, await api.ScalarAsync<long>(
            "SELECT count(*) FROM buyers.sessions WHERE revoked_reason = 'password_reset'"));
        // No abre sesión: siguen siendo las dos de antes.
        Assert.Equal(2, await api.ScalarAsync<long>("SELECT count(*) FROM buyers.sessions"));
        Assert.Equal(
            HttpStatusCode.Unauthorized, (await api.PostAsync("/session", new { email = Email, password = OldPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync("/session", new { email = Email, password = NewPassword })).StatusCode);
    }

    [Fact]
    public async Task ResetVerifiesAnUnverifiedAccount()
    {
        await using var api = await OpenAsync();
        (await RegisterUnverifiedAsync(api)).EnsureSuccessStatusCode();
        await ForgotAsync(api);

        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(api, await LatestResetTokenAsync(api))).StatusCode);

        Assert.True(await api.ScalarAsync<bool>("SELECT email_verified_at IS NOT NULL FROM buyers.accounts"));
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync("/session", new { email = Email, password = NewPassword })).StatusCode);
    }

    [Fact]
    public async Task AVerifyTokenDoesNotReset()
    {
        await using var api = await OpenAsync();
        (await RegisterUnverifiedAsync(api)).EnsureSuccessStatusCode();
        var verifyPayloads = await api.OutboxPayloadsAsync("buyers.email-verification-requested.v1");
        var verifyToken = verifyPayloads[^1]["token"]!.GetValue<string>();

        await AssertTokenInvalidAsync(await ResetAsync(api, verifyToken));
        Assert.False(await api.ScalarAsync<bool>("SELECT email_verified_at IS NOT NULL FROM buyers.accounts"));
    }

    [Fact]
    public async Task AShortPasswordDoesNotBurnTheToken()
    {
        await using var api = await OpenAsync();
        await api.RegisterVerifiedAsync();
        await ForgotAsync(api);
        var token = await LatestResetTokenAsync(api);

        var rejected = await ResetAsync(api, token, newPassword: "corta");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        var body = await JsonAsync(rejected);
        Assert.Equal("validation.failed", (string?)body["code"]);
        Assert.NotNull(body["errors"]!["NewPassword"]);
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(api, token)).StatusCode);
    }

    [Fact]
    public async Task ABlankTokenIs422ValidationFailed()
    {
        await using var api = await OpenAsync();

        var response = await ResetAsync(api, "");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.NotNull((await JsonAsync(response))["errors"]!["Token"]);
    }

    [Fact]
    public async Task AReusedResetTokenIs422()
    {
        await using var api = await OpenAsync();
        await api.RegisterVerifiedAsync();
        await ForgotAsync(api);
        var token = await LatestResetTokenAsync(api);
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(api, token)).StatusCode);

        await AssertTokenInvalidAsync(await ResetAsync(api, token, "una-tercera-contrasena"));
    }

    [Fact]
    public async Task AnUnknownResetTokenIs422()
    {
        await using var api = await OpenAsync();

        await AssertTokenInvalidAsync(await ResetAsync(api, "no-existe"));
    }

    [Fact]
    public async Task AResetTokenOlderThanOneHourIs422()
    {
        await using var api = await OpenAsync();
        await api.RegisterVerifiedAsync();
        await ForgotAsync(api);
        var token = await LatestResetTokenAsync(api);
        await api.ScalarAsync<long>(
            "WITH u AS (UPDATE buyers.email_tokens SET expires_at = now() - interval '1 minute' WHERE purpose = 'Reset' RETURNING 1) SELECT count(*) FROM u");

        await AssertTokenInvalidAsync(await ResetAsync(api, token));
        Assert.Equal(
            HttpStatusCode.OK, (await api.PostAsync("/session", new { email = Email, password = OldPassword })).StatusCode);
    }

    [Fact]
    public async Task ForgotWithAFailedCaptchaIs422AndWritesNothing()
    {
        await using var api = await OpenAsync();
        await api.RegisterVerifiedAsync();
        api.Captcha.Passes = false;

        var response = await ForgotAsync(api);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("store.buyer.captcha_invalid", (string?)(await JsonAsync(response))["code"]);
        Assert.Empty(await api.OutboxPayloadsAsync(Reset));
    }

    [Fact]
    public async Task ForgotWithoutTheVisitorIpIs422VisitorIpRequired()
    {
        await using var api = await OpenAsync();

        var response = await ForgotAsync(api, visitorIp: null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("store.public.visitor_ip_required", (string?)(await JsonAsync(response))["code"]);
    }

    [Fact]
    public async Task ForgotOnAnUnknownHostIs404()
    {
        await using var api = await OpenAsync();

        var response = await api.PostAsync(
            "/password/forgot", new { email = Email, turnstileToken = "token-ok" }, host: FakeBuyerStorefronts.MissingHost);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ForgotWithAnInvalidBodyIs422ValidationFailed()
    {
        await using var api = await OpenAsync();

        var response = await ForgotAsync(api, email: "no-es-un-correo", turnstile: "");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var errors = (await JsonAsync(response))["errors"]!;
        Assert.NotNull(errors["Email"]);
        Assert.NotNull(errors["TurnstileToken"]);
    }
}
