using System.Net;
using System.Text.Json.Nodes;
using static Modules.Buyers.IntegrationTests.BuyersApiHarness;

namespace Modules.Buyers.IntegrationTests;

/// <summary>
/// <c>POST</c>, <c>GET</c> y <c>DELETE /buyer/session</c> de punta a punta. Necesitan Docker.
/// </summary>
public sealed class BuyerSessionApiTests
{
    private const string Email = "ana@correo.com";
    private const string Host = "acme.lulocrm.com";
    private const string OtherHost = "otra.lulocrm.com";
    private const string Verify = "buyers.email-verification-requested.v1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> LoginAsync(
        BuyersApi api, string email = Email, string password = "una-contrasena-larga", string host = Host) =>
        api.PostAsync("/session", new { email, password }, host);

    private static async Task<JsonNode> JsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;

    private static async Task<string> LoginTokenAsync(BuyersApi api, string host = Host)
    {
        var response = await LoginAsync(api, host: host);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (string)(await JsonAsync(response))["sessionToken"]!;
    }

    private static async Task AssertSessionInvalidAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("store.buyer.session_invalid", (string?)(await JsonAsync(response))["code"]);
    }

    [Fact]
    public async Task LoginReturnsATokenThatReadsTheBuyer()
    {
        await using var api = await OpenAsync();
        await api.RegisterVerifiedAsync();

        var response = await LoginAsync(api);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        var token = (string)body["sessionToken"]!;
        Assert.Equal(43, token.Length);
        Assert.Equal(Email, (string?)body["buyer"]!["email"]);
        var expiresAt = DateTimeOffset.Parse((string)body["expiresAt"]!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(expiresAt, DateTimeOffset.UtcNow.AddDays(30).AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30).AddMinutes(5));

        var read = await api.SendAsync(HttpMethod.Get, "/session", token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(Email, (string?)(await JsonAsync(read))["email"]);

        Assert.Equal(1, await api.ScalarAsync<long>("SELECT count(*) FROM buyers.sessions WHERE token_hash <> $1", token));
        Assert.Equal(0, await api.ScalarAsync<long>("SELECT count(*) FROM buyers.sessions WHERE token_hash = $1", token));
    }

    [Fact]
    public async Task LoginNormalizesEmailCaseAndWhitespace()
    {
        await using var api = await OpenAsync();
        await api.RegisterVerifiedAsync();

        var response = await LoginAsync(api, email: "  ANA@Correo.COM ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Email, (string?)(await JsonAsync(response))["buyer"]!["email"]);
    }

    [Fact]
    public async Task AWrongPasswordAndAnUnknownEmailGiveTheSame401()
    {
        await using var api = await OpenAsync();
        await api.RegisterVerifiedAsync();

        var wrong = await LoginAsync(api, password: "otra-contrasena-larga");
        var unknown = await LoginAsync(api, email: "nadie@correo.com");
        var malformed = await LoginAsync(api, email: "no-es-un-email");

        foreach (var response in new[] { wrong, unknown, malformed })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var expected = await JsonAsync(wrong);
        Assert.Equal("store.buyer.credentials_invalid", (string?)expected["code"]);
        foreach (var response in new[] { unknown, malformed })
        {
            var body = await JsonAsync(response);
            Assert.Equal((string?)expected["code"], (string?)body["code"]);
            Assert.Equal((string?)expected["title"], (string?)body["title"]);
        }
    }

    [Fact]
    public async Task AnUnverifiedAccountWithTheRightPasswordIs403AndQueuesNothing()
    {
        await using var api = await OpenAsync();
        (await api.PostAsync(
            "/register",
            new { email = Email, password = "una-contrasena-larga", acceptsDataPolicy = true, turnstileToken = "ok", profile = (object?)null }))
            .EnsureSuccessStatusCode();
        var queued = (await api.OutboxPayloadsAsync(Verify)).Count;

        var response = await LoginAsync(api);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("store.buyer.email_not_verified", (string?)(await JsonAsync(response))["code"]);
        Assert.Equal(queued, (await api.OutboxPayloadsAsync(Verify)).Count);
        Assert.Equal(0, await api.ScalarAsync<long>("SELECT count(*) FROM buyers.sessions"));
    }

    [Fact]
    public async Task AnUnverifiedAccountWithTheWrongPasswordIs401()
    {
        await using var api = await OpenAsync();
        (await api.PostAsync(
            "/register",
            new { email = Email, password = "una-contrasena-larga", acceptsDataPolicy = true, turnstileToken = "ok", profile = (object?)null }))
            .EnsureSuccessStatusCode();

        var response = await LoginAsync(api, password: "otra-contrasena-larga");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("store.buyer.credentials_invalid", (string?)(await JsonAsync(response))["code"]);
    }

    [Fact]
    public async Task EmptyCredentialsAre422()
    {
        await using var api = await OpenAsync();

        var response = await api.PostAsync("/session", new { email = "", password = "" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", (string?)(await JsonAsync(response))["code"]);
    }

    [Fact]
    public async Task ASessionIsRejectedOnAnotherHost()
    {
        await using var api = await OpenAsync();
        await api.RegisterVerifiedAsync();
        var token = await LoginTokenAsync(api);

        await AssertSessionInvalidAsync(await api.SendAsync(HttpMethod.Get, "/session", token, OtherHost));
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Get, "/session", token)).StatusCode);
    }

    [Fact]
    public async Task AMissingOrUnknownTokenIs401()
    {
        await using var api = await OpenAsync();

        await AssertSessionInvalidAsync(await api.SendAsync(HttpMethod.Get, "/session"));
        await AssertSessionInvalidAsync(await api.SendAsync(HttpMethod.Get, "/session", "desconocido"));
        await AssertSessionInvalidAsync(await api.SendAsync(HttpMethod.Get, "/session", new string('a', 500)));
    }

    [Fact]
    public async Task LogoutRevokesAndIsIdempotent()
    {
        await using var api = await OpenAsync();
        await api.RegisterVerifiedAsync();
        var token = await LoginTokenAsync(api);

        var first = await api.SendAsync(HttpMethod.Delete, "/session", token);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        await AssertSessionInvalidAsync(await api.SendAsync(HttpMethod.Get, "/session", token));
        Assert.Equal(HttpStatusCode.NoContent, (await api.SendAsync(HttpMethod.Delete, "/session", token)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.SendAsync(HttpMethod.Delete, "/session")).StatusCode);
        Assert.Equal("logout", await api.ScalarAsync<string>("SELECT revoked_reason FROM buyers.sessions"));
    }

    [Fact]
    public async Task LogoutOnAnotherHostDoesNotRevoke()
    {
        await using var api = await OpenAsync();
        await api.RegisterVerifiedAsync();
        var token = await LoginTokenAsync(api);

        var response = await api.SendAsync(HttpMethod.Delete, "/session", token, OtherHost);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Get, "/session", token)).StatusCode);
    }

    [Fact]
    public async Task AnIdleSessionIs401()
    {
        await using var api = await OpenAsync();
        await api.RegisterVerifiedAsync();
        var token = await LoginTokenAsync(api);
        await api.ScalarAsync<long>("UPDATE buyers.sessions SET last_seen_at = last_seen_at - interval '8 days'");

        await AssertSessionInvalidAsync(await api.SendAsync(HttpMethod.Get, "/session", token));
    }

    [Fact]
    public async Task ASessionOfADeletedAccountIs401()
    {
        await using var api = await OpenAsync();
        await api.RegisterVerifiedAsync();
        var token = await LoginTokenAsync(api);
        await api.ScalarAsync<long>("DELETE FROM buyers.accounts");

        await AssertSessionInvalidAsync(await api.SendAsync(HttpMethod.Get, "/session", token));
    }

    [Fact]
    public async Task EveryBuyerResponseIsNoStore()
    {
        await using var api = await OpenAsync();
        await api.RegisterVerifiedAsync();

        var login = await LoginAsync(api);
        var token = (string)(await JsonAsync(login))["sessionToken"]!;
        var read = await api.SendAsync(HttpMethod.Get, "/session", token);
        var rejected = await api.SendAsync(HttpMethod.Get, "/session");

        Assert.True(login.Headers.CacheControl?.NoStore);
        Assert.True(read.Headers.CacheControl?.NoStore);
        Assert.True(rejected.Headers.CacheControl?.NoStore);
    }
}
