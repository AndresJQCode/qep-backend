using System.Net;
using System.Text.Json.Nodes;
using static Modules.Buyers.IntegrationTests.BuyersApiHarness;

namespace Modules.Buyers.IntegrationTests;

/// <summary>
/// <c>POST /buyer/account/delete</c> de punta a punta. Necesitan Docker.
/// </summary>
public sealed class BuyerAccountDeletionApiTests
{
    private const string Email = "ana@correo.com";
    private const string Password = "una-contrasena-larga";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonNode> JsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;

    private static Task<long> CountAsync(BuyersApi api, string table) =>
        api.ScalarAsync<long>($"SELECT count(*) FROM buyers.{table}");

    private static async Task<(BuyersApi Api, string Token)> OpenWithSessionAsync()
    {
        var api = await OpenAsync();
        await api.RegisterVerifiedAsync();
        var login = await api.PostAsync("/session", new { email = Email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (api, (string)(await JsonAsync(login))["sessionToken"]!);
    }

    private static Task<HttpResponseMessage> DeleteAsync(BuyersApi api, string? token, string password = Password) =>
        api.PostAsync("/account/delete", new { password }, session: token);

    [Fact]
    public async Task DeleteRemovesAccountProfileSessionsAndTokens()
    {
        var (api, token) = await OpenWithSessionAsync();
        await using var _ = api;
        (await api.PutAsync("/profile", new
        {
            billing = new
            {
                name = "Ana Pérez", identificationType = "CC", identificationNumber = "1020304050", phone = "3001234567",
                email = "facturas@empresa.com", address = "Cra 1 # 2-3", country = "CO", departmentId = Guid.NewGuid(),
                cityId = Guid.NewGuid(), cityName = (string?)null,
            },
            shipping = (object?)null,
            shippingSameAsBilling = true,
        }, token)).EnsureSuccessStatusCode();
        foreach (var table in new[] { "accounts", "profiles", "sessions", "email_tokens" })
        {
            Assert.True(await CountAsync(api, table) > 0, table);
        }

        var response = await DeleteAsync(api, token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        foreach (var table in new[] { "accounts", "profiles", "sessions", "email_tokens" })
        {
            Assert.Equal(0, await CountAsync(api, table));
        }
    }

    [Fact]
    public async Task DeleteKeepsTheConsentRecord()
    {
        var (api, token) = await OpenWithSessionAsync();
        await using var _ = api;

        (await DeleteAsync(api, token)).EnsureSuccessStatusCode();

        Assert.Equal(1, await CountAsync(api, "consents"));
        Assert.Equal(Email, await api.ScalarAsync<string>("SELECT email FROM buyers.consents"));
        Assert.False(string.IsNullOrEmpty(await api.ScalarAsync<string>("SELECT visitor_ip FROM buyers.consents")));
        Assert.Equal("acme.lulocrm.com", await api.ScalarAsync<string>("SELECT store_host FROM buyers.consents"));
        Assert.Equal(
            "https://acme.lulocrm.com/cuenta/politica", await api.ScalarAsync<string>("SELECT policy_url FROM buyers.consents"));
        Assert.False(string.IsNullOrEmpty(await api.ScalarAsync<string>("SELECT policy_version FROM buyers.consents")));
    }

    [Fact]
    public async Task DeleteQueuesTheAccountDeletedEmail()
    {
        var (api, token) = await OpenWithSessionAsync();
        await using var _ = api;

        (await DeleteAsync(api, token)).EnsureSuccessStatusCode();

        var payload = Assert.Single(await api.OutboxPayloadsAsync("buyers.account-deleted.v1"));
        Assert.Equal(Email, (string?)payload["email"]);
        Assert.Equal("acme.lulocrm.com", (string?)payload["storeHost"]);
        Assert.Equal("Tienda Demo", (string?)payload["storeName"]);
        Assert.Null(payload["token"]);
    }

    // I1 residual B1: un correo del comprador que sigue en el outbox se entregaría después del de cuenta eliminada y dejaría
    // una notificación con el correo. Se borra en la misma transacción que la cuenta; los de otra cuenta no se tocan.
    [Fact]
    public async Task DeleteDropsTheAccountsPendingBuyerEmailsFromTheOutbox()
    {
        var (api, token) = await OpenWithSessionAsync();
        await using var _ = api;
        await api.RegisterVerifiedAsync("otra@correo.com");
        (await api.PostAsync("/password/forgot", new { email = Email, turnstileToken = "token-ok" })).EnsureSuccessStatusCode();
        (await api.PostAsync("/password/forgot", new { email = "otra@correo.com", turnstileToken = "token-ok" }))
            .EnsureSuccessStatusCode();
        foreach (var email in new[] { Email, "otra@correo.com" })
        {
            (await api.PostAsync(
                "/register",
                new { email, password = Password, acceptsDataPolicy = true, turnstileToken = "token-ok", profile = (object?)null }))
                .EnsureSuccessStatusCode();
        }

        string[] pending =
        [
            "buyers.email-verification-requested.v1", "buyers.password-reset-requested.v1", "buyers.existing-account-notice.v1",
        ];
        foreach (var eventName in pending)
        {
            Assert.Contains(await api.OutboxPayloadsAsync(eventName), p => (string?)p["email"] == Email);
        }

        (await DeleteAsync(api, token)).EnsureSuccessStatusCode();

        foreach (var eventName in pending)
        {
            var payloads = await api.OutboxPayloadsAsync(eventName);
            Assert.DoesNotContain(payloads, p => (string?)p["email"] == Email);
            Assert.Contains(payloads, p => (string?)p["email"] == "otra@correo.com");
        }

        Assert.Single(await api.OutboxPayloadsAsync("buyers.account-deleted.v1"));
    }

    [Fact]
    public async Task TheSessionIsInvalidAfterDelete()
    {
        var (api, token) = await OpenWithSessionAsync();
        await using var _ = api;

        (await DeleteAsync(api, token)).EnsureSuccessStatusCode();

        var read = await api.SendAsync(HttpMethod.Get, "/session", token);
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        Assert.Equal("store.buyer.session_invalid", (string?)(await JsonAsync(read))["code"]);
        var login = await api.PostAsync("/session", new { email = Email, password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal("store.buyer.credentials_invalid", (string?)(await JsonAsync(login))["code"]);
    }

    [Fact]
    public async Task TheEmailCanRegisterAgainAfterDelete()
    {
        var (api, token) = await OpenWithSessionAsync();
        await using var _ = api;
        (await DeleteAsync(api, token)).EnsureSuccessStatusCode();

        var again = await api.PostAsync(
            "/register",
            new { email = Email, password = Password, acceptsDataPolicy = true, turnstileToken = "token-ok", profile = (object?)null });

        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Equal(1, await CountAsync(api, "accounts"));
        Assert.Null(await api.ScalarAsync<object>("SELECT email_verified_at FROM buyers.accounts"));
    }

    [Fact]
    public async Task AWrongPasswordIs422WithErrorsPasswordAndDeletesNothing()
    {
        var (api, token) = await OpenWithSessionAsync();
        await using var _ = api;

        var response = await DeleteAsync(api, token, "no-es-la-contrasena");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("store.buyer.password_incorrect", (string?)body["code"]);
        Assert.NotNull(body["errors"]!["Password"]);
        Assert.Equal(1, await CountAsync(api, "accounts"));
        Assert.Empty(await api.OutboxPayloadsAsync("buyers.account-deleted.v1"));
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Get, "/session", token)).StatusCode);
    }

    [Fact]
    public async Task WithoutAValidSessionForTheHostIs401AndDeletesNothing()
    {
        var (api, token) = await OpenWithSessionAsync();
        await using var _ = api;

        foreach (var response in new[]
        {
            await DeleteAsync(api, null),
            await DeleteAsync(api, "no-existe"),
            await api.PostAsync("/account/delete", new { password = Password }, "otra.lulocrm.com", token),
        })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("store.buyer.session_invalid", (string?)(await JsonAsync(response))["code"]);
        }

        Assert.Equal(1, await CountAsync(api, "accounts"));
    }
}
