using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using static Modules.Buyers.IntegrationTests.BuyersApiHarness;

namespace Modules.Buyers.IntegrationTests;

/// <summary>
/// <c>POST /buyer/register</c> de punta a punta: la API real, Postgres y el limitador en su orden de middleware real. El host y
/// Turnstile son los dobles del harness. Necesitan Docker.
/// </summary>
public sealed class BuyerRegistrationApiTests
{
    private const string Password = "una-contrasena-larga";
    private const string Verify = "buyers.email-verification-requested.v1";
    private const string Existing = "buyers.existing-account-notice.v1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Body(
        string email = "ana@correo.com",
        string password = Password,
        bool accepts = true,
        string turnstile = "token-ok",
        object? profile = null) =>
        new { email, password, acceptsDataPolicy = accepts, turnstileToken = turnstile, profile };

    private static object ValidProfile(string name = "Ana Pérez") => new
    {
        billing = new
        {
            name,
            identificationType = "CC",
            identificationNumber = "1020304050",
            phone = "3001234567",
            email = "ana@correo.com",
            address = "Cra 1 # 2-3",
            country = "CO",
            departmentId = Guid.NewGuid(),
            cityId = Guid.NewGuid(),
            cityName = (string?)null,
        },
        shipping = (object?)null,
        shippingSameAsBilling = true,
    };

    private static async Task<JsonNode> JsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;

    private static Task<long> CountAsync(BuyersApi api, string table) =>
        api.ScalarAsync<long>($"SELECT count(*) FROM buyers.{table}");

    private static Task<int> VerifyEmailAsync(BuyersApi api, string email) =>
        api.ScalarAsync<int>(
            "WITH updated AS (UPDATE buyers.accounts SET email_verified_at = now() WHERE email = $1 RETURNING 1) SELECT count(*)::int FROM updated",
            email);

    [Fact]
    public async Task RegisterReturns202AndNoStore()
    {
        await using var api = await OpenAsync();

        var response = await api.PostAsync("/register", Body());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, await CountAsync(api, "accounts"));
        var payload = Assert.Single(await api.OutboxPayloadsAsync(Verify));
        Assert.Equal("ana@correo.com", (string?)payload["email"]);
        Assert.Equal("acme.lulocrm.com", (string?)payload["storeHost"]);
        Assert.Equal("Tienda Demo", (string?)payload["storeName"]);
        Assert.False(string.IsNullOrEmpty((string?)payload["token"]));
    }

    [Fact]
    public async Task RegisterRecordsTheConsentWithTheVisitorIpAndThePolicyUrl()
    {
        await using var api = await OpenAsync();

        var response = await api.PostAsync("/register", Body(profile: ValidProfile()), visitorIp: "203.0.113.55");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("203.0.113.55", await api.ScalarAsync<string>("SELECT visitor_ip FROM buyers.consents"));
        Assert.Equal(
            "https://acme.lulocrm.com/cuenta/politica", await api.ScalarAsync<string>("SELECT policy_url FROM buyers.consents"));
        Assert.Equal(1, await CountAsync(api, "profiles"));
    }

    [Fact]
    public async Task RegisterWithAnExistingVerifiedEmailReturnsTheSame202()
    {
        await using var api = await OpenAsync();
        var fresh = await api.PostAsync("/register", Body(email: "nueva@correo.com"));
        await api.PostAsync("/register", Body());
        await VerifyEmailAsync(api, "ana@correo.com");

        var existing = await api.PostAsync("/register", Body(password: "otra-contrasena-larga"));

        Assert.Equal(fresh.StatusCode, existing.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, existing.StatusCode);
        Assert.Equal(await fresh.Content.ReadAsStringAsync(Ct), await existing.Content.ReadAsStringAsync(Ct));
        Assert.Equal(2, await CountAsync(api, "accounts"));
        var notice = Assert.Single(await api.OutboxPayloadsAsync(Existing));
        Assert.Equal("ana@correo.com", (string?)notice["email"]);
        Assert.Null(notice["token"]);
    }

    [Fact]
    public async Task RegisterWithAnUppercasedExistingEmailSendsTheExistingAccountNotice()
    {
        await using var api = await OpenAsync();
        await api.PostAsync("/register", Body());
        await VerifyEmailAsync(api, "ana@correo.com");

        var response = await api.PostAsync("/register", Body(email: "  ANA@Correo.COM "));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, await CountAsync(api, "accounts"));
        var notice = Assert.Single(await api.OutboxPayloadsAsync(Existing));
        Assert.Equal("ana@correo.com", (string?)notice["email"]);
    }

    [Fact]
    public async Task RegisterNormalizesEmailCaseAndWhitespaceOntoAnUnverifiedAccount()
    {
        await using var api = await OpenAsync();
        await api.PostAsync("/register", Body());

        var response = await api.PostAsync("/register", Body(email: " ANA@correo.com\t", password: "segunda-contrasena-larga"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, await CountAsync(api, "accounts"));
        Assert.Equal(2, (await api.OutboxPayloadsAsync(Verify)).Count);
        Assert.Equal(1, await api.ScalarAsync<long>("SELECT count(*) FROM buyers.email_tokens WHERE used_at IS NULL"));
    }

    [Fact]
    public async Task RegisterWithoutConsentIs422ConsentRequired()
    {
        await using var api = await OpenAsync();

        var response = await api.PostAsync("/register", Body(accepts: false));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("store.buyer.consent_required", (string?)(await JsonAsync(response))["code"]);
        Assert.Equal(0, await CountAsync(api, "accounts"));
        Assert.Equal(0, api.Captcha.Calls);
    }

    [Fact]
    public async Task AFailedCaptchaIs422AndWritesNothing()
    {
        await using var api = await OpenAsync();
        api.Captcha.Passes = false;

        var response = await api.PostAsync("/register", Body());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("store.buyer.captcha_invalid", (string?)(await JsonAsync(response))["code"]);
        Assert.Equal(1, api.Captcha.Calls);
        Assert.Equal(0, await CountAsync(api, "accounts"));
        Assert.Equal(0, await CountAsync(api, "consents"));
        Assert.Empty(await api.OutboxPayloadsAsync(Verify));
    }

    [Fact]
    public async Task AShortPasswordIs422WithErrorsPassword()
    {
        await using var api = await OpenAsync();

        var response = await api.PostAsync("/register", Body(password: "corta"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = await JsonAsync(response);
        Assert.Equal("validation.failed", (string?)json["code"]);
        Assert.NotNull(json["errors"]!["Password"]);
        Assert.Equal(0, api.Captcha.Calls);
        Assert.Equal(0, await CountAsync(api, "accounts"));
    }

    [Fact]
    public async Task AnInvalidProfileIs422WithAPrefixedKey()
    {
        await using var api = await OpenAsync();
        var profile = ValidProfile();
        var broken = System.Text.Json.JsonSerializer.SerializeToNode(profile)!;
        broken["billing"]!["email"] = "no-es-un-correo";

        var response = await api.PostAsync("/register", Body(profile: broken));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.NotNull((await JsonAsync(response))["errors"]!["Profile.Billing.Email"]);
        Assert.Equal(0, await CountAsync(api, "accounts"));
    }

    [Fact]
    public async Task RegisterWithoutVisitorIpIs422()
    {
        await using var api = await OpenAsync();

        var response = await api.PostAsync("/register", Body(), visitorIp: null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("store.public.visitor_ip_required", (string?)(await JsonAsync(response))["code"]);
        Assert.Equal(0, api.Captcha.Calls);
        Assert.Equal(0, await CountAsync(api, "accounts"));
    }

    [Fact]
    public async Task AnUnknownHostIs404StorePublicNotFound()
    {
        await using var api = await OpenAsync();

        var response = await api.PostAsync("/register", Body(), host: FakeBuyerStorefronts.MissingHost);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("store.public.not_found", (string?)(await JsonAsync(response))["code"]);
        Assert.Equal(0, api.Captcha.Calls);
    }

    [Fact]
    public async Task WithoutTheStorefrontKeyIs401()
    {
        await using var api = await OpenAsync();
        using var anonymous = api.Factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync(
            "/api/v1/public/stores/acme.lulocrm.com/buyer/register", Body(), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("store.public.client_unauthorized", (string?)(await JsonAsync(response))["code"]);
        Assert.Equal(0, await CountAsync(api, "accounts"));
    }

    [Fact]
    public async Task EveryBuyerResponseIsNoStoreEvenOn4xx()
    {
        await using var api = await OpenAsync();
        using var anonymous = api.Factory.CreateClient();

        var unauthorized = await anonymous.PostAsJsonAsync(
            "/api/v1/public/stores/acme.lulocrm.com/buyer/register", Body(), Ct);
        var invalid = await api.PostAsync("/register", Body(password: "corta"));
        var noConsent = await api.PostAsync("/register", Body(accepts: false));
        var notFound = await api.PostAsync("/register", Body(), host: FakeBuyerStorefronts.MissingHost);
        var accepted = await api.PostAsync("/register", Body());

        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noConsent.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        foreach (var response in new[] { unauthorized, invalid, noConsent, notFound, accepted })
        {
            Assert.True(response.Headers.CacheControl?.NoStore, $"{(int)response.StatusCode} lacks Cache-Control: no-store");
        }
    }

    private static Task<HttpResponseMessage> PostRawAsync(BuyersApi api, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/public/stores/acme.lulocrm.com/buyer/register")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        return api.Storefront.SendAsync(request, Ct);
    }

    [Fact]
    public async Task AMalformedJsonBodyIs400AndNoStore()
    {
        await using var api = await OpenAsync();

        var response = await PostRawAsync(api, "{ \"email\": ");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore, "400 lacks Cache-Control: no-store");
        Assert.Equal(0, await CountAsync(api, "accounts"));
    }

    [Fact]
    public async Task ABodyOver64KbIs413AndNoStore()
    {
        await using var api = await OpenAsync();

        var response = await PostRawAsync(api, "{\"email\":\"" + new string('a', 70_000) + "\"}");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore, "413 lacks Cache-Control: no-store");
        Assert.Equal(0, await CountAsync(api, "accounts"));
    }

    [Fact]
    public async Task TheEleventhRegisterInAMinuteFromOneVisitorIs429ButAnotherVisitorPasses()
    {
        await using var api = await OpenAsync();
        const string visitor = "198.51.100.77";

        // 422 por contraseña corta: no escribe nada y cuenta igual contra el cupo de la ruta.
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            var response = await api.PostAsync("/register", Body(password: "corta"), visitorIp: visitor);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        }

        var limited = await api.PostAsync("/register", Body(), visitorIp: visitor);
        var other = await api.PostAsync("/register", Body(), visitorIp: "198.51.100.78");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.CacheControl?.NoStore, "429 lacks Cache-Control: no-store");
        Assert.Equal(HttpStatusCode.Accepted, other.StatusCode);
        Assert.Equal(1, await CountAsync(api, "accounts"));
    }
}
