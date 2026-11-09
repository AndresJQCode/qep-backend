using System.Net;
using System.Text.Json.Nodes;
using static Modules.Buyers.IntegrationTests.BuyersApiHarness;

namespace Modules.Buyers.IntegrationTests;

/// <summary>
/// <c>PUT /buyer/profile</c> y <c>POST /buyer/password</c> de punta a punta. Necesitan Docker.
/// </summary>
public sealed class BuyerAccountApiTests
{
    private const string Email = "ana@correo.com";
    private const string Host = "acme.lulocrm.com";
    private const string OtherHost = "otra.lulocrm.com";
    private const string OldPassword = "una-contrasena-larga";
    private const string NewPassword = "otra-contrasena-nueva";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Profile(
        string name = "Ana Pérez", string email = "facturas@empresa.com", string identificationType = "CC", string country = "CO") => new
        {
            billing = new
            {
                name,
                identificationType,
                identificationNumber = "1020304050",
                phone = "3001234567",
                email,
                address = "Cra 1 # 2-3",
                country,
                departmentId = country == "CO" ? Guid.NewGuid() : (Guid?)null,
                cityId = country == "CO" ? Guid.NewGuid() : (Guid?)null,
                cityName = country == "CO" ? null : "Madrid",
            },
            shipping = (object?)null,
            shippingSameAsBilling = true,
        };

    private static async Task<JsonNode> JsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;

    private static async Task<string> LoginTokenAsync(
        BuyersApi api, string password = OldPassword, string host = Host)
    {
        var response = await api.PostAsync("/session", new { email = Email, password }, host);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (string)(await JsonAsync(response))["sessionToken"]!;
    }

    private static async Task<(BuyersApi Api, string Token)> OpenWithSessionAsync()
    {
        var api = await OpenAsync();
        await api.RegisterVerifiedAsync();
        return (api, await LoginTokenAsync(api));
    }

    private static async Task AssertSessionInvalidAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("store.buyer.session_invalid", (string?)(await JsonAsync(response))["code"]);
    }

    [Fact]
    public async Task UpdateProfileReturnsTheBuyerWithTheProfile()
    {
        var (api, token) = await OpenWithSessionAsync();
        await using var _ = api;

        var response = await api.PutAsync("/profile", Profile("Ana María"), token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("Ana María", (string?)body["profile"]!["billing"]!["name"]);
        var read = await JsonAsync(await api.SendAsync(HttpMethod.Get, "/session", token));
        Assert.Equal("Ana María", (string?)read["profile"]!["billing"]!["name"]);

        // Reemplaza, no duplica.
        (await api.PutAsync("/profile", Profile("Ana Otra"), token)).EnsureSuccessStatusCode();
        Assert.Equal(1, await api.ScalarAsync<long>("SELECT count(*) FROM buyers.profiles"));
        read = await JsonAsync(await api.SendAsync(HttpMethod.Get, "/session", token));
        Assert.Equal("Ana Otra", (string?)read["profile"]!["billing"]!["name"]);
    }

    // M1: los códigos se normalizan en BuyerProfileWriter, el único punto por el que pasa todo perfil guardado.
    [Fact]
    public async Task RegisterAndUpdateProfileStoreTheCodesNormalized()
    {
        await using var api = await OpenAsync();
        var lowercase = new
        {
            billing = new
            {
                name = "Ana Pérez",
                identificationType = " cc ",
                identificationNumber = "1020304050",
                phone = "3001234567",
                email = "facturas@empresa.com",
                address = "Cra 1 # 2-3",
                country = "co",
                departmentId = Guid.NewGuid(),
                cityId = Guid.NewGuid(),
                cityName = (string?)null,
            },
            shipping = new
            {
                recipientName = "Luis",
                phone = "3009876543",
                address = "Calle Mayor 1",
                country = " es ",
                departmentId = (Guid?)null,
                cityId = (Guid?)null,
                cityName = "Madrid",
            },
            shippingSameAsBilling = false,
        };

        var registered = await api.PostAsync(
            "/register",
            new { email = Email, password = OldPassword, acceptsDataPolicy = true, turnstileToken = "token-ok", profile = lowercase });
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        await AssertStoredCodesAsync(api, "CC", "CO", "ES");

        var verifyToken = (await api.OutboxPayloadsAsync("buyers.email-verification-requested.v1"))[^1]["token"]!.GetValue<string>();
        (await api.PostAsync("/email/verify", new { token = verifyToken })).EnsureSuccessStatusCode();
        var token = await LoginTokenAsync(api);
        (await api.PutAsync("/profile", Profile(), token)).EnsureSuccessStatusCode();
        await AssertStoredCodesAsync(api, "CC", "CO", null);

        (await api.PutAsync("/profile", lowercase, token)).EnsureSuccessStatusCode();
        await AssertStoredCodesAsync(api, "CC", "CO", "ES");
    }

    private static async Task AssertStoredCodesAsync(BuyersApi api, string type, string country, string? shippingCountry)
    {
        Assert.Equal(type, await api.ScalarAsync<string>("SELECT data->'billing'->>'identificationType' FROM buyers.profiles"));
        Assert.Equal(country, await api.ScalarAsync<string>("SELECT data->'billing'->>'country' FROM buyers.profiles"));
        Assert.Equal(shippingCountry, await api.ScalarAsync<string?>("SELECT data->'shipping'->>'country' FROM buyers.profiles"));
    }

    [Fact]
    public async Task UpdateProfileDoesNotChangeTheLoginEmail()
    {
        var (api, token) = await OpenWithSessionAsync();
        await using var _ = api;

        var response = await api.PutAsync("/profile", Profile(email: "otro@empresa.com"), token);

        var body = await JsonAsync(response);
        Assert.Equal(Email, (string?)body["email"]);
        Assert.Equal("otro@empresa.com", (string?)body["profile"]!["billing"]!["email"]);
        Assert.Equal(Email, await api.ScalarAsync<string>("SELECT email FROM buyers.accounts"));
    }

    [Fact]
    public async Task AnInvalidProfileIs422WithFieldErrors()
    {
        var (api, token) = await OpenWithSessionAsync();
        await using var _ = api;
        var broken = System.Text.Json.JsonSerializer.SerializeToNode(Profile())!;
        broken["billing"]!["name"] = string.Empty;

        var response = await api.PutAsync("/profile", broken, token);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("validation.failed", (string?)body["code"]);
        Assert.NotNull(body["errors"]!["Billing.Name"]);
        Assert.Equal(0, await api.ScalarAsync<long>("SELECT count(*) FROM buyers.profiles"));
    }

    [Fact]
    public async Task UpdateProfileStoresASpanishNif()
    {
        var (api, token) = await OpenWithSessionAsync();
        await using var _ = api;

        var response = await api.PutAsync("/profile", Profile(identificationType: "NIF", country: "ES"), token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("NIF", await api.ScalarAsync<string>("SELECT data->'billing'->>'identificationType' FROM buyers.profiles"));
    }

    [Fact]
    public async Task UpdateProfileWithAColombianTypeForSpainIs422()
    {
        var (api, token) = await OpenWithSessionAsync();
        await using var _ = api;

        var response = await api.PutAsync("/profile", Profile(identificationType: "CC", country: "ES"), token);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("validation.failed", (string?)body["code"]);
        Assert.NotNull(body["errors"]!["Billing.IdentificationType"]);
        Assert.Equal(0, await api.ScalarAsync<long>("SELECT count(*) FROM buyers.profiles"));
    }

    [Fact]
    public async Task ALegacyProfileStillReads()
    {
        var (api, token) = await OpenWithSessionAsync();
        await using var _ = api;
        (await api.PutAsync("/profile", Profile(), token)).EnsureSuccessStatusCode();
        // Un perfil guardado antes de la regla por país: España con un tipo colombiano.
        await api.ScalarAsync<int>(
            "UPDATE buyers.profiles SET data = jsonb_set(jsonb_set(data, '{billing,country}', '\"ES\"'), '{billing,identificationType}', '\"CC\"') RETURNING 1");

        var response = await api.SendAsync(HttpMethod.Get, "/session", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var billing = (await JsonAsync(response))["profile"]!["billing"]!;
        Assert.Equal("CC", (string?)billing["identificationType"]);
        Assert.Equal("ES", (string?)billing["country"]);
    }

    [Fact]
    public async Task UpdateProfileWithoutSessionIs401()
    {
        await using var api = await OpenAsync();
        await api.RegisterVerifiedAsync();

        await AssertSessionInvalidAsync(await api.PutAsync("/profile", Profile()));
        await AssertSessionInvalidAsync(await api.PutAsync("/profile", Profile(), session: "no-existe"));
        Assert.Equal(0, await api.ScalarAsync<long>("SELECT count(*) FROM buyers.profiles"));
    }

    [Fact]
    public async Task ASessionOfAnotherHostCannotUpdateTheProfile()
    {
        var (api, token) = await OpenWithSessionAsync();
        await using var _ = api;
        (await api.PutAsync("/profile", Profile("Ana Original"), token)).EnsureSuccessStatusCode();

        var response = await api.PutAsync("/profile", Profile("Intruso"), token, OtherHost);

        await AssertSessionInvalidAsync(response);
        var read = await JsonAsync(await api.SendAsync(HttpMethod.Get, "/session", token));
        Assert.Equal("Ana Original", (string?)read["profile"]!["billing"]!["name"]);
    }

    [Fact]
    public async Task ChangePasswordKeepsTheCurrentSessionAndRevokesTheOthers()
    {
        var (api, tokenA) = await OpenWithSessionAsync();
        await using var _ = api;
        var tokenB = await LoginTokenAsync(api);

        var response = await api.PostAsync(
            "/password", new { currentPassword = OldPassword, newPassword = NewPassword }, session: tokenA);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Get, "/session", tokenA)).StatusCode);
        await AssertSessionInvalidAsync(await api.SendAsync(HttpMethod.Get, "/session", tokenB));
        Assert.Equal(
            "password_changed",
            await api.ScalarAsync<string>("SELECT revoked_reason FROM buyers.sessions WHERE revoked_at IS NOT NULL"));

        var old = await api.PostAsync("/session", new { email = Email, password = OldPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        var fresh = await api.PostAsync("/session", new { email = Email, password = NewPassword });
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [Fact]
    public async Task AWrongCurrentPasswordIs422WithErrorsCurrentPasswordAndKeepsTheSession()
    {
        var (api, token) = await OpenWithSessionAsync();
        await using var _ = api;

        var response = await api.PostAsync(
            "/password", new { currentPassword = "no-es-la-contrasena", newPassword = NewPassword }, session: token);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("store.buyer.password_incorrect", (string?)body["code"]);
        Assert.NotNull(body["errors"]!["CurrentPassword"]);
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Get, "/session", token)).StatusCode);
        var login = await api.PostAsync("/session", new { email = Email, password = OldPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task AShortNewPasswordIs422WithErrorsNewPassword()
    {
        var (api, token) = await OpenWithSessionAsync();
        await using var _ = api;

        var response = await api.PostAsync(
            "/password", new { currentPassword = OldPassword, newPassword = "corta" }, session: token);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("validation.failed", (string?)body["code"]);
        Assert.NotNull(body["errors"]!["NewPassword"]);
        var login = await api.PostAsync("/session", new { email = Email, password = OldPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task ChangePasswordWithoutSessionIs401()
    {
        await using var api = await OpenAsync();
        await api.RegisterVerifiedAsync();

        await AssertSessionInvalidAsync(
            await api.PostAsync("/password", new { currentPassword = OldPassword, newPassword = NewPassword }));
    }
}
