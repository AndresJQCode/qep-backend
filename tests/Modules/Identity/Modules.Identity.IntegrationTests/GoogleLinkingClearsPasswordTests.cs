using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Identity.IntegrationTests.PasswordAuthApiHarness;

namespace Modules.Identity.IntegrationTests;

/// <summary>
/// «El ataque»: alguien pre-registra con contraseña el email de su víctima. Cuando la víctima
/// entra con Google, la credencial del atacante tiene que desaparecer y sus tokens quedar quemados.
/// </summary>
public sealed class GoogleLinkingClearsPasswordTests
{
    [Fact]
    public async Task GoogleLoginOnAnUnverifiedPasswordUserDeletesTheCredentialAndBurnsTokens()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var client = CreateClient(factory);
        var email = NewEmail();
        var userId = await SeedUnverifiedPasswordUserAsync(factory, email, "attacker-password");

        var response = await PostWithGoogleAsync(
            client, "/api/v1/auth/session", IssueGoogleIdToken("victim-sub", email, true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await CountRowsAsync(cs, "identity.password_credentials", userId));
        Assert.Equal(0, await CountUnusedTokensAsync(cs, userId));
        Assert.NotNull(await EmailVerifiedAtAsync(cs, userId));
    }

    [Fact]
    public async Task GoogleRegistrationOnAnUnverifiedPasswordUserDeletesTheCredentialAndBurnsTokens()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var client = CreateClient(factory);
        var email = NewEmail();
        var userId = await SeedUnverifiedPasswordUserAsync(factory, email, "attacker-password");

        var response = await PostWithGoogleAsync(
            client,
            "/api/v1/auth/register-tenant",
            IssueGoogleIdToken("victim-sub", email, true),
            new { displayName = "Acme", slug = NewSlug() });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(0, await CountRowsAsync(cs, "identity.password_credentials", userId));
        Assert.Equal(0, await CountUnusedTokensAsync(cs, userId));
        Assert.NotNull(await EmailVerifiedAtAsync(cs, userId));
    }

    [Fact]
    public async Task GoogleRegistrationWithoutAnEmailClaimIsStillTheSame403()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var client = CreateClient(factory);

        var response = await PostWithGoogleAsync(
            client,
            "/api/v1/auth/register-tenant",
            IssueGoogleIdToken("sub-1", "", true),
            new { displayName = "Acme", slug = NewSlug() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        Assert.Equal("email_not_verified", problem.GetProperty("detail").GetString());
    }
}
