using System.Net;
using System.Text.Json;
using Modules.Identity.Application;
using static Modules.Identity.IntegrationTests.PasswordAuthApiHarness;

namespace Modules.Identity.IntegrationTests;

/// <summary>
/// Registro y login con email y contraseña contra el host real. Cada prueba levanta su propia base
/// y su propia fábrica: el limitador de tasa comparte la partición `unknown` en TestServer.
/// </summary>
public sealed class PasswordAuthApiTests
{
    private const string Pw = "correct horse battery";

    [Fact]
    public async Task RegisterWithANewEmailCreatesTheTenantAndQueuesOneVerificationMail()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var client = CreateClient(factory);
        var ct = TestContext.Current.CancellationToken;
        var email = NewEmail();
        var slug = NewSlug();

        var response = await RegisterAsync(client, email, Pw, slug);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("{}", await response.Content.ReadAsStringAsync(ct));
        var userId = await FindUserIdAsync(cs, email);
        Assert.Null(await EmailVerifiedAtAsync(cs, userId));
        Assert.Equal(1, await CountRowsAsync(cs, "identity.password_credentials", userId));
        Assert.Single(await ReadOutboxAsync(cs, IdentityIntegrationEvents.EmailVerificationRequested, userId));
        Assert.Equal(1, await CountTenantsWithSlugAsync(cs, slug));
        Assert.Equal(1, await CountAuditAsync(cs, userId, "identity.password.registered"));
    }

    [Fact]
    public async Task RegisterWithAnExistingEmailCreatesNothingAndQueuesTheExistingAccountMail()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var client = CreateClient(factory);
        var ct = TestContext.Current.CancellationToken;
        var email = NewEmail();
        var slug1 = NewSlug();
        var slug2 = NewSlug();

        await RegisterAsync(client, email, Pw, slug1);
        var userId = await FindUserIdAsync(cs, email);
        var hashBefore = await CredentialHashAsync(cs, userId);
        var second = await RegisterAsync(client, "  " + email.ToUpperInvariant(), "another long password", slug2);

        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal("{}", await second.Content.ReadAsStringAsync(ct));
        Assert.Equal(0, await CountTenantsWithSlugAsync(cs, slug2));
        Assert.Equal(hashBefore, await CredentialHashAsync(cs, userId));
        Assert.Single(await ReadOutboxAsync(cs, IdentityIntegrationEvents.ExistingAccountNotice, userId));
        // Sólo el del primer registro.
        Assert.Single(await ReadOutboxAsync(cs, IdentityIntegrationEvents.EmailVerificationRequested, userId));
    }

    [Fact]
    public async Task RegisterWithATakenSlugReturnsTheSame422ForNewAndExistingEmails()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var client = CreateClient(factory);
        var email = NewEmail();
        var takenSlug = NewSlug();

        await RegisterAsync(client, email, Pw, takenSlug);
        var withNewEmail = await RegisterAsync(client, NewEmail(), Pw, takenSlug);
        var withExistingEmail = await RegisterAsync(client, email, Pw, takenSlug);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, withNewEmail.StatusCode);
        Assert.Equal("tenancy.slug.taken", (await ProblemAsync(withNewEmail)).Code);
        Assert.Equal("tenancy.slug.taken", (await ProblemAsync(withExistingEmail)).Code);
        Assert.Equal(withNewEmail.StatusCode, withExistingEmail.StatusCode);
    }

    [Fact]
    public async Task RegisterValidatesThePasswordBeforeTheSlug()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var client = CreateClient(factory);
        var takenSlug = NewSlug();

        await RegisterAsync(client, NewEmail(), Pw, takenSlug);
        var response = await RegisterAsync(client, NewEmail(), "short", takenSlug);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal("validation.failed", problem.Code);
        // ApiExceptionHandler.cs:66 agrupa por PropertyName de FluentValidation: llega "Password", no "password".
        // El frontend lee esa clave; no cambiar la política de nombres sin tocar los dos lados.
        Assert.True(problem.Errors!.ContainsKey("Password"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    public async Task RegisterReportsASingleErrorOnThePasswordFieldForEmptyAndShortPasswords(string password)
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var client = CreateClient(factory);

        var response = await RegisterAsync(client, NewEmail(), password, NewSlug());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal("validation.failed", problem.Code);
        Assert.Single(problem.Errors!["Password"]);
    }

    [Fact]
    public async Task RegisterWithAMalformedEmailFailsOnTheEmailFieldAndCreatesNothing()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var client = CreateClient(factory);
        var slug = NewSlug();

        var response = await RegisterAsync(client, "not-an-email", Pw, slug);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal("validation.failed", problem.Code);
        Assert.True(problem.Errors!.ContainsKey("Email"));
        Assert.Equal(0, await CountTenantsWithSlugAsync(cs, slug));
    }

    [Fact]
    public async Task ConcurrentRegistrationsWithTheSameNewEmailBothAnswer202AndCreateOneUser()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var client = CreateClient(factory);
        var email = NewEmail();
        var slug1 = NewSlug();
        var slug2 = NewSlug();

        var responses = await Task.WhenAll(
            RegisterAsync(client, email, Pw, slug1),
            RegisterAsync(client, email, Pw, slug2));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        Assert.Equal(1, await CountUsersWithEmailAsync(cs, email));
        var userId = await FindUserIdAsync(cs, email);
        // El perdedor siguió el camino de email existente: un aviso, un solo tenant.
        Assert.Single(await ReadOutboxAsync(cs, IdentityIntegrationEvents.ExistingAccountNotice, userId));
        Assert.Equal(
            1,
            await CountTenantsWithSlugAsync(cs, slug1) + await CountTenantsWithSlugAsync(cs, slug2));
    }

    [Fact]
    public async Task RegisterIsForbiddenWhenSignupIsDisabled()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var disabled = new PasswordAuthApiFactory(cs, signupEnabled: false);
        var email = NewEmail();
        var slug = NewSlug();

        var response = await RegisterAsync(CreateClient(disabled), email, Pw, slug);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Public tenant registration is disabled.", (await ProblemAsync(response)).Title);
        Assert.Equal(0, await CountUsersWithEmailAsync(cs, email));
        Assert.Equal(0, await CountTenantsWithSlugAsync(cs, slug));
    }

    // Review Focus 1
    [Fact]
    public async Task ConcurrentRegistrationsWithTheSameSlugCreateOneTenantAndQueueOneVerificationMail()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var client = CreateClient(factory);
        var slug = NewSlug();

        var responses = await Task.WhenAll(
            RegisterAsync(client, NewEmail(), Pw, slug),
            RegisterAsync(client, NewEmail(), Pw, slug));

        Assert.Equal(
            [HttpStatusCode.Accepted, HttpStatusCode.UnprocessableEntity],
            responses.Select(r => r.StatusCode).Order());
        Assert.Equal(
            "tenancy.slug.taken",
            (await ProblemAsync(responses.Single(r => r.StatusCode == HttpStatusCode.UnprocessableEntity))).Code);
        Assert.Equal(1, await CountTenantsWithSlugAsync(cs, slug));
        Assert.Equal(1, await CountOutboxAsync(cs, IdentityIntegrationEvents.EmailVerificationRequested));
    }

    // Review Focus 5
    [Fact]
    public async Task TheVerificationOutboxPayloadCarriesOnlyTheUserIdAndTheToken()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var client = CreateClient(factory);
        var email = NewEmail();

        await RegisterAsync(client, email, Pw, NewSlug());
        var userId = await FindUserIdAsync(cs, email);
        var payload = JsonDocument.Parse(
            Assert.Single(await ReadOutboxAsync(cs, IdentityIntegrationEvents.EmailVerificationRequested, userId)));

        Assert.Equal(["token", "userId"], payload.RootElement.EnumerateObject().Select(p => p.Name).Order());
        var token = payload.RootElement.GetProperty("token").GetString()!;
        Assert.Equal(1, await CountTokensWithHashAsync(cs, EmailTokenCodec.Hash(token)));
        Assert.DoesNotContain(Pw, payload.RootElement.GetRawText());
        Assert.DoesNotContain(email, payload.RootElement.GetRawText());
    }
}
