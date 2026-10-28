using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Identity.Application;
using Modules.Identity.Domain;
using Modules.Identity.Infrastructure.Persistence;
using Modules.Tenancy.Application;
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
    public async Task RegisterWithAnExistingEmailCreatesTheNewTenantForThatUserAndQueuesTheExistingAccountMail()
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
        Assert.Equal(1, await CountTenantsWithSlugAsync(cs, slug2));
        Assert.Equal(1, await CountUsersWithEmailAsync(cs, email));
        // La contraseña del segundo request no pisa la credencial: el usuario ya existía.
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

    [Fact]
    public async Task ANonSlugFailureCreatingTheTenantLeavesNoUserBehind()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        await using var failing = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddScoped<ITenantRegistration>(provider =>
                new ThrowingRegistration(
                    ActivatorUtilities.CreateInstance<TenantRegistrationService>(provider)))));
        using var failingClient = failing.CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var email = NewEmail();

        var response = await RegisterAsync(failingClient, email, Pw, NewSlug());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0, await CountUsersWithEmailAsync(cs, email));
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
        // El perdedor siguió el camino de email existente: un aviso, y su tenant se crea igual.
        Assert.Single(await ReadOutboxAsync(cs, IdentityIntegrationEvents.ExistingAccountNotice, userId));
        Assert.Equal(
            2,
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

    [Fact]
    public async Task ASlugRaceLoserLeavesNoUserBehindAndCanRegisterAgainWithAFreshSlug()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var client = CreateClient(factory);
        // Salta el chequeo previo del slug: el request llega a RegisterOwnerTenantAsync con un slug que
        // ya existe, que es exactamente lo que ve el perdedor de una carrera.
        await using var racing = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddScoped<ITenantRegistration>(provider =>
                new SkipEnsureRegistration(
                    ActivatorUtilities.CreateInstance<TenantRegistrationService>(provider)))));
        using var racingClient = racing.CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var takenSlug = NewSlug();
        var loserEmail = NewEmail();
        await RegisterAsync(client, NewEmail(), Pw, takenSlug);

        var lost = await RegisterAsync(racingClient, loserEmail, Pw, takenSlug);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, lost.StatusCode);
        Assert.Equal("tenancy.slug.taken", (await ProblemAsync(lost)).Code);
        Assert.Equal(0, await CountUsersWithEmailAsync(cs, loserEmail));

        var freshSlug = NewSlug();
        var retry = await RegisterAsync(client, loserEmail, Pw, freshSlug);

        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        Assert.Equal(1, await CountTenantsWithSlugAsync(cs, freshSlug));
        var userId = await FindUserIdAsync(cs, loserEmail);
        Assert.Single(await ReadOutboxAsync(cs, IdentityIntegrationEvents.EmailVerificationRequested, userId));
    }

    [Theory]
    [InlineData("text/plain", "not json", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("application/json", "{ not json", HttpStatusCode.BadRequest)]
    public async Task RegisterWithANonJsonContentTypeReturns415AndAMalformedBodyReturns400(
        string contentType,
        string body,
        HttpStatusCode expected)
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var client = CreateClient(factory);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/password/register-tenant")
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };
        request.Headers.Add("X-Qep-Client", "web");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData(null, "Acme", "tenancy.slug.invalid")]
    [InlineData("a-valid-slug-1", null, "tenancy.settings.display_name.invalid")]
    public async Task RegisterWithANullSlugOrDisplayNameReturns422AndCreatesNoUser(
        string? slug,
        string? displayName,
        string expectedCode)
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var client = CreateClient(factory);
        var email = NewEmail();

        var response = await PostJsonAsync(
            client,
            "/api/v1/auth/password/register-tenant",
            new { email, password = Pw, displayName, slug });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(expectedCode, (await ProblemAsync(response)).Code);
        Assert.Equal(0, await CountUsersWithEmailAsync(cs, email));
    }

    [Fact]
    public async Task TheUnitOfWorkTranslatesARealEmailUniqueViolationAndClearsTheTracker()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        _ = factory.Services; // levanta el host, que aplica las migraciones
        var email = NewEmail();
        var now = DateTimeOffset.UtcNow;
        await using var winner = NewIdentityContext(cs);
        await using var loser = NewIdentityContext(cs);
        winner.Users.Add(User.CreateInvited(UserId.New(), email, now));
        loser.Users.Add(User.CreateInvited(UserId.New(), email, now));
        await new IdentityUnitOfWork(winner).SaveChangesAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<IdentityDomainException>(() =>
            new IdentityUnitOfWork(loser).SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal("identity.user.email_taken", exception.Code);
        Assert.Empty(loser.ChangeTracker.Entries());
    }

    [Fact]
    public async Task RegisterThenVerifyFromTheCapturedMailOpensASession()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        var channel = new RecordingEmailChannel();
        await using var factory = new PasswordAuthApiFactory(cs, channel);
        using var client = CreateClient(factory);
        var ct = TestContext.Current.CancellationToken;
        var email = NewEmail();

        await RegisterAsync(client, email, Pw, NewSlug());
        var userId = await FindUserIdAsync(cs, email);
        await WaitUntilAsync(() => Task.FromResult(channel.Sent.Count == 1), TimeSpan.FromSeconds(30)); // el worker sondea cada 15 s
        var token = System.Text.RegularExpressions.Regex.Match(
            Assert.Single(channel.Sent).TextBody, @"verify-email\?token=([A-Za-z0-9_-]+)").Groups[1].Value;

        var response = await VerifyAsync(client, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith("qep_session=", StringComparison.Ordinal));
        var me = await client.GetFromJsonAsync<SessionPayload>("/api/v1/auth/me", ct);
        Assert.Single(me!.ActiveTenants);
        Assert.Equal(1, await CountAuditAsync(cs, userId, "identity.email.verified"));
        Assert.NotNull(await EmailVerifiedAtAsync(cs, userId));
    }

    [Fact]
    public async Task VerifyWithAnUnknownExpiredOrUsedTokenReturnsTheSame422()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var client = CreateClient(factory);
        var expiredEmail = NewEmail();
        var usedEmail = NewEmail();

        await RegisterAsync(client, expiredEmail, Pw, NewSlug());
        await RegisterAsync(client, usedEmail, Pw, NewSlug());
        var expiredUser = await FindUserIdAsync(cs, expiredEmail);
        var usedUser = await FindUserIdAsync(cs, usedEmail);
        var expiredToken = await LatestOutboxTokenAsync(
            cs, IdentityIntegrationEvents.EmailVerificationRequested, expiredUser);
        var usedToken = await LatestOutboxTokenAsync(
            cs, IdentityIntegrationEvents.EmailVerificationRequested, usedUser);
        await using (var connection = new Npgsql.NpgsqlConnection(cs))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var expire = new Npgsql.NpgsqlCommand(
                "UPDATE identity.email_tokens SET expires_at = now() - interval '1 minute' WHERE user_id = @expiredUser",
                connection);
            expire.Parameters.AddWithValue("expiredUser", expiredUser);
            await expire.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(client, usedToken)).StatusCode);

        var unknown = await VerifyAsync(client, "nope");
        var expired = await VerifyAsync(client, expiredToken);
        var used = await VerifyAsync(client, usedToken); // segundo uso

        Assert.All(
            new[] { unknown, expired, used },
            r => Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode));
        var problems = new[] { await ProblemAsync(unknown), await ProblemAsync(expired), await ProblemAsync(used) };
        Assert.All(problems, p => Assert.Equal("identity.email_token.invalid", p.Code));
        Assert.Single(problems.Select(p => (p.Title, p.Detail)).Distinct());
    }

    // Review Focus 3
    [Fact]
    public async Task ConcurrentVerifyWithTheSameTokenOpensExactlyOneSession()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var registrar = CreateClient(factory);
        using var clientA = CreateClient(factory);
        using var clientB = CreateClient(factory);
        var email = NewEmail();

        await RegisterAsync(registrar, email, Pw, NewSlug());
        var userId = await FindUserIdAsync(cs, email);
        var token = await LatestOutboxTokenAsync(
            cs, IdentityIntegrationEvents.EmailVerificationRequested, userId);

        var responses = await Task.WhenAll(VerifyAsync(clientA, token), VerifyAsync(clientB, token));

        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.UnprocessableEntity],
            responses.Select(r => r.StatusCode).Order());
        Assert.Equal(1, await CountRowsAsync(cs, "identity.sessions", userId));
    }

    // Decora el servicio real saltando el chequeo previo; el registro del tenant sigue siendo el real.
    [Fact]
    public async Task LoginBeforeVerifyingReturns403AndReissuesTheVerifyToken()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var client = CreateClient(factory);
        var email = NewEmail();

        await RegisterAsync(client, email, Pw, NewSlug());
        var userId = await FindUserIdAsync(cs, email);
        var firstToken = await LatestOutboxTokenAsync(cs, IdentityIntegrationEvents.EmailVerificationRequested, userId);

        var response = await LoginAsync(client, email, Pw);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("identity.email.not_verified", (await ProblemAsync(response)).Code);
        Assert.Equal(2, (await ReadOutboxAsync(cs, IdentityIntegrationEvents.EmailVerificationRequested, userId)).Count);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await VerifyAsync(client, firstToken)).StatusCode); // el anterior quedó invalidado
    }

    [Fact]
    public async Task AWrongPasswordAnUnknownEmailAndAGoogleOnlyUserGetTheSame401()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var client = CreateClient(factory);
        var email = NewEmail();
        await RegisterAndVerifyAsync(client, cs, email, Pw);
        var googleEmail = NewEmail();
        await PostWithGoogleAsync(
            CreateClient(factory),
            "/api/v1/auth/register-tenant",
            IssueGoogleIdToken("g-sub", googleEmail, true),
            new { displayName = "Acme", slug = NewSlug() });

        var wrongPassword = await LoginAsync(CreateClient(factory), email, "not the right password");
        var unknownEmail = await LoginAsync(CreateClient(factory), NewEmail(), Pw);
        var googleOnly = await LoginAsync(CreateClient(factory), googleEmail, Pw);

        var expected = await ShapeAsync(wrongPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, expected.Status);
        Assert.Equal("identity.credentials.invalid", expected.Code);
        Assert.Equal(expected, await ShapeAsync(unknownEmail));
        Assert.Equal(expected, await ShapeAsync(googleOnly));
    }

    [Fact]
    public async Task LoginAfterVerifyingOpensASessionAndIsAudited()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var client = CreateClient(factory);
        var ct = TestContext.Current.CancellationToken;
        var email = NewEmail();
        var userId = (await RegisterAndVerifyAsync(CreateClient(factory), cs, email, Pw)).UserId;

        var response = await LoginAsync(client, email, Pw); // cliente nuevo, sin cookie

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith("qep_session=", StringComparison.Ordinal));
        Assert.Equal(userId, (await response.Content.ReadFromJsonAsync<SessionPayload>(ct))!.UserId);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me", ct)).StatusCode);
        Assert.Equal(1, await CountAuditAsync(cs, userId, "identity.password.login.succeeded"));
    }

    // Review Focus 2
    [Fact]
    public async Task AUserRegisteredWithAWhitespaceOnlyPasswordCanLogIn()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var client = CreateClient(factory);
        var email = NewEmail();
        const string spaces = "          ";
        await RegisterAndVerifyAsync(CreateClient(factory), cs, email, spaces);

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, email, spaces)).StatusCode);
    }

    [Fact]
    public async Task LoginNormalizesEmailCaseAndWhitespace()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var client = CreateClient(factory);
        var email = NewEmail();
        await RegisterAndVerifyAsync(CreateClient(factory), cs, email, Pw);

        Assert.Equal(
            HttpStatusCode.OK,
            (await LoginAsync(client, "  " + email.ToUpperInvariant() + " ", Pw)).StatusCode);
    }

    [Fact]
    public async Task AFailedLoginForAnUnknownEmailIsAuditedWithoutTheEmail()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var client = CreateClient(factory);
        var unknownEmail = NewEmail();

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, unknownEmail, Pw)).StatusCode);

        Assert.Equal(1, await CountAuditByResourceAsync(cs, "identity.password.login.failed", "unknown"));
        Assert.Equal(0, await CountAuditContainingAsync(cs, unknownEmail));
        Assert.Equal(0, await CountAuditContainingAsync(cs, Pw));
    }

    [Fact]
    public async Task ForgotWithAnUnknownEmailReturns202AndQueuesNothing()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var client = CreateClient(factory);

        Assert.Equal(HttpStatusCode.Accepted, (await ForgotAsync(client, NewEmail())).StatusCode);

        Assert.Equal(0, await CountOutboxAsync(cs, IdentityIntegrationEvents.PasswordResetRequested));
    }

    // Review Focus 2
    [Fact]
    public async Task ForgotNormalizesTheEmail()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var client = CreateClient(factory);
        var email = NewEmail();
        var userId = (await RegisterAndVerifyAsync(CreateClient(factory), cs, email, Pw)).UserId;

        Assert.Equal(
            HttpStatusCode.Accepted,
            (await ForgotAsync(client, "  " + email.ToUpperInvariant() + " ")).StatusCode);

        Assert.Single(await ReadOutboxAsync(cs, IdentityIntegrationEvents.PasswordResetRequested, userId));
        Assert.Equal(1, await CountAuditAsync(cs, userId, "identity.password.reset.requested"));
    }

    [Fact]
    public async Task ResetRevokesEarlierSessionsAndOnlyTheNewPasswordLogsIn()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        var ct = TestContext.Current.CancellationToken;
        var clientA = CreateClient(factory);
        var clientB = CreateClient(factory);
        var email = NewEmail();
        const string newPassword = "a brand new password";
        var userId = (await RegisterAndVerifyAsync(clientA, cs, email, Pw)).UserId; // clientA con sesión viva

        await ForgotAsync(clientB, email);
        var token = await LatestOutboxTokenAsync(cs, IdentityIntegrationEvents.PasswordResetRequested, userId);

        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(clientB, token, newPassword)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await clientA.GetAsync("/api/v1/auth/me", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(clientB, email, Pw)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(clientB, email, newPassword)).StatusCode);
        Assert.Equal(1, await CountAuditAsync(cs, userId, "identity.password.reset.completed"));
    }

    [Fact]
    public async Task ResetWithAShortPasswordFailsValidationAndKeepsTheToken()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var client = CreateClient(factory);
        var email = NewEmail();
        var userId = (await RegisterAndVerifyAsync(CreateClient(factory), cs, email, Pw)).UserId;
        await ForgotAsync(client, email);
        var token = await LatestOutboxTokenAsync(cs, IdentityIntegrationEvents.PasswordResetRequested, userId);

        var shortReset = await ResetAsync(client, token, "short");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, shortReset.StatusCode);
        var problem = await ProblemAsync(shortReset);
        Assert.Equal("validation.failed", problem.Code);
        Assert.True(problem.Errors!.ContainsKey("Password"));
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await ResetAsync(client, token, "a long enough password")).StatusCode);
    }

    [Fact]
    public async Task ResetVerifiesAnUnverifiedEmail()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var client = CreateClient(factory);
        var email = NewEmail();
        const string newPassword = "a brand new password";
        await RegisterAsync(client, email, Pw, NewSlug()); // sin verificar
        var userId = await FindUserIdAsync(cs, email);
        await ForgotAsync(client, email);
        var token = await LatestOutboxTokenAsync(cs, IdentityIntegrationEvents.PasswordResetRequested, userId);

        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(client, token, newPassword)).StatusCode);

        Assert.NotNull(await EmailVerifiedAtAsync(cs, userId));
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, email, newPassword)).StatusCode); // no 403
    }

    [Fact]
    public async Task AnInvitedUserCreatesAPasswordAndEndsActive()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var client = CreateClient(factory);
        var ownerClient = CreateClient(factory);
        const string newPassword = "a brand new password";
        var owner = await RegisterAndVerifyAsync(ownerClient, cs, NewEmail(), Pw);
        var invitedEmail = NewEmail();
        var invited = await InviteAsync(ownerClient, cs, owner.TenantId, invitedEmail);
        await ForgotAsync(client, invitedEmail);
        var token = await LatestOutboxTokenAsync(
            cs, IdentityIntegrationEvents.PasswordResetRequested, invited.UserId);

        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(client, token, newPassword)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, invitedEmail, newPassword)).StatusCode);
        Assert.Equal("Active", await MembershipStateAsync(cs, owner.TenantId, invited.UserId));
        Assert.Equal("Active", await UserStatusAsync(cs, invited.UserId));
    }

    // Carry-over de la Task 11: el propósito del token se compara en el consumo, así que uno emitido
    // para un fin no sirve en el otro endpoint. Los dos dan el 422 genérico y el token queda intacto.
    [Fact]
    public async Task ATokenMintedForOnePurposeIsRejectedByTheOtherEndpointWithTheGeneric422()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var client = CreateClient(factory);
        var email = NewEmail();
        await RegisterAsync(client, email, Pw, NewSlug()); // deja un token verify vivo
        var userId = await FindUserIdAsync(cs, email);
        var verifyToken = await LatestOutboxTokenAsync(
            cs, IdentityIntegrationEvents.EmailVerificationRequested, userId);
        await ForgotAsync(client, email);
        var resetToken = await LatestOutboxTokenAsync(
            cs, IdentityIntegrationEvents.PasswordResetRequested, userId);

        var resetOnVerify = await VerifyAsync(client, resetToken);
        var verifyOnReset = await ResetAsync(client, verifyToken, "a brand new password");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resetOnVerify.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, verifyOnReset.StatusCode);
        var problems = new[] { await ProblemAsync(resetOnVerify), await ProblemAsync(verifyOnReset) };
        Assert.All(problems, p => Assert.Equal("identity.email_token.invalid", p.Code));
        Assert.Single(problems.Select(p => (p.Title, p.Detail)).Distinct());
        // Ninguno se consumió: cada uno sigue sirviendo en su propio endpoint.
        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(client, verifyToken)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await ResetAsync(client, resetToken, "a brand new password")).StatusCode);
    }

    [Fact]
    public async Task TheEleventhRequestToOneEndpointIs429AndAnotherEndpointStillAnswers()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var client = CreateClient(factory);

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await ForgotAsync(client, NewEmail())).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await ForgotAsync(client, NewEmail())).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await LoginAsync(client, NewEmail(), "whatever password")).StatusCode);
    }

    // Cierre del tercer anti-objetivo: ni la auditoría ni el registro de fallas guardan una contraseña,
    // un token crudo o el hash de un token. notifications.notifications no tiene columna de cuerpo
    // (sólo destinatario, plantilla, estado y failure_reason), así que esa revisión cubre esas columnas
    // y no el contenido de los correos. La outbox queda exenta por decisión del spec.
    [Fact]
    public async Task NoAuditEntryOrFailureLogCarriesAPasswordOrAToken()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        var channel = new RecordingEmailChannel();
        await using var factory = new PasswordAuthApiFactory(cs, channel);
        using var client = CreateClient(factory);
        var email = NewEmail();
        const string oldPassword = "the original secret phrase";
        const string newPassword = "a completely new secret phrase";

        await RegisterAsync(client, email, oldPassword, NewSlug());
        var userId = await FindUserIdAsync(cs, email);
        var verifyToken = await LatestOutboxTokenAsync(
            cs, IdentityIntegrationEvents.EmailVerificationRequested, userId);
        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(client, verifyToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, email, oldPassword)).StatusCode);

        Assert.Equal(HttpStatusCode.Accepted, (await ForgotAsync(client, email)).StatusCode);
        var resetToken = await LatestOutboxTokenAsync(
            cs, IdentityIntegrationEvents.PasswordResetRequested, userId);
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(client, resetToken, newPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, email, newPassword)).StatusCode);

        await WaitUntilAsync(() => Task.FromResult(channel.Sent.Count >= 2), TimeSpan.FromSeconds(45)); // el worker sondea cada 15 s

        // Requests that fail while the secrets are in hand, so the failure log has something to leak.
        await ResetAsync(client, "not-a-real-token", newPassword);
        await VerifyAsync(client, verifyToken); // already used: 422

        var audit = await AllAuditTextAsync(cs);
        var notifications = await AllNotificationTextAsync(cs);
        var failures = await AllRequestFailureTextAsync(cs);
        foreach (var secret in new[] { oldPassword, newPassword, verifyToken, resetToken })
        {
            Assert.DoesNotContain(secret, audit);
            Assert.DoesNotContain(secret, notifications);
            Assert.DoesNotContain(secret, failures);
        }

        Assert.DoesNotContain(EmailTokenCodec.Hash(verifyToken), audit);
        Assert.DoesNotContain(EmailTokenCodec.Hash(verifyToken), failures);
    }

    private sealed class ThrowingRegistration(ITenantRegistration inner) : ITenantRegistration
    {
        public Task<Guid> RegisterOwnerTenantAsync(
            Guid ownerUserId,
            TenantRegistrationData data,
            string correlationId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated transient failure");

        public Task EnsureRegistrableAsync(TenantRegistrationData data, CancellationToken cancellationToken) =>
            inner.EnsureRegistrableAsync(data, cancellationToken);
    }

    private sealed class SkipEnsureRegistration(ITenantRegistration inner) : ITenantRegistration
    {
        public Task<Guid> RegisterOwnerTenantAsync(
            Guid ownerUserId,
            TenantRegistrationData data,
            string correlationId,
            CancellationToken cancellationToken) =>
            inner.RegisterOwnerTenantAsync(ownerUserId, data, correlationId, cancellationToken);

        public Task EnsureRegistrableAsync(TenantRegistrationData data, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
