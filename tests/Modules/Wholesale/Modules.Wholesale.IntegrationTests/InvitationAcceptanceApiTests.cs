using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Wholesale.Application;

namespace Modules.Wholesale.IntegrationTests;

/// <summary>
/// El link de la invitación de punta a punta (contrato «Invitación», D-3, D-4, D-16, D-17, D-20; spec 2026-11-19): resumen
/// anónimo, el código del correo, las dos sesiones, los tenants vinculables y la aceptación con sus escrituras entre módulos.
/// Cada prueba usa un correo propio para que el usuario de una no exista en otra del mismo host.
/// </summary>
public sealed class InvitationAcceptanceApiTests(SharedWholesaleHost host) : IClassFixture<SharedWholesaleHost>
{
    private const string Password = "Distri-2026!";

    private const string EmailChangedEvent = "wholesale.distributor-email-changed.v1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WholesaleApiHarness.QepApiFactory Factory => host.Factory;

    [Fact]
    public async Task TheSummaryIsAnonymousAndShowsPendingThenAccepted()
    {
        var email = NewEmail();
        var (_, distributor, token) = await WholesaleApiHarness.InvitedAsync(Factory, email);
        using var anonymous = WholesaleApiHarness.CreateAnonymous(Factory);

        var pending = await anonymous.GetFromJsonAsync<WholesaleInvitationResponse>(WholesaleApiHarness.InvitationUrl(token), Ct);
        var userId = await WholesaleApiHarness.PasswordSessionAsync(Factory, token, Password);
        (await AcceptNewTenantAsync(userId, token)).EnsureSuccessStatusCode();
        var accepted = await anonymous.GetFromJsonAsync<WholesaleInvitationResponse>(WholesaleApiHarness.InvitationUrl(token), Ct);

        Assert.Equal(
            ("Mayorista API Tests", "Distribuciones Norte SAS", "Ana Pérez", email, "pending", false),
            (pending!.WholesalerName, pending.BusinessName, pending.ContactName, pending.Email, pending.Status, pending.HasPassword));
        Assert.Equal(distributor.Invitation.ExpiresAt, pending.ExpiresAt);
        Assert.Equal(("accepted", true), (accepted!.Status, accepted.HasPassword));
    }

    [Fact]
    public async Task AnExpiredLinkIs410()
    {
        var email = NewEmail();
        var (_, distributor, token) = await WholesaleApiHarness.InvitedAsync(Factory, email);
        await WholesaleApiHarness.ExecuteAsync(
            Factory,
            $"""
            UPDATE wholesale.distributor_invitations SET expires_at = now() - interval '1 minute'
            WHERE distributor_id = {distributor.Id}
            """);
        using var anonymous = WholesaleApiHarness.CreateAnonymous(Factory);

        var summary = await anonymous.GetFromJsonAsync<WholesaleInvitationResponse>(WholesaleApiHarness.InvitationUrl(token), Ct);
        var session = await anonymous.PostAsJsonAsync(
            $"{WholesaleApiHarness.InvitationUrl(token)}/session-with-password",
            new { email, password = Password, code = "000000", termsVersion = WholesaleApiHarness.TermsVersion },
            Ct);

        Assert.Equal("expired", summary!.Status);
        Assert.Equal(HttpStatusCode.Gone, session.StatusCode);
        Assert.Equal("wholesale.invitation.expired", await WholesaleApiHarness.CodeOfAsync(session));
    }

    // Spec 2026-11-19 §4 y foco 1: sin contraseña, el código y los términos son obligatorios; después, la contraseña sola.
    [Fact]
    public async Task ANewDistributorFinishesWithAPasswordACodeAndTheTerms()
    {
        var email = NewEmail();
        var (_, _, token) = await WholesaleApiHarness.InvitedAsync(Factory, email);
        using var anonymous = WholesaleApiHarness.CreateAnonymous(Factory);
        var url = $"{WholesaleApiHarness.InvitationUrl(token)}/session-with-password";

        var missingBody = await anonymous.PostAsync(
            url, new StringContent("{", System.Text.Encoding.UTF8, "application/json"), Ct);
        var withoutCode = await anonymous.PostAsJsonAsync(
            url, new { email, password = Password, termsVersion = WholesaleApiHarness.TermsVersion }, Ct);
        var usersAfterNoCode = await CountUsersAsync(email);
        // Un código inventado sin términos es el mismo 401: los términos se miran después de gastar un código válido.
        var fakeCodeWithoutTerms = await anonymous.PostAsJsonAsync(url, new { email, password = Password, code = "000000" }, Ct);
        var code = await WholesaleApiHarness.EmailCodeAsync(Factory, token, email);
        var response = await anonymous.PostAsJsonAsync(
            url, new { email, password = Password, code, termsVersion = WholesaleApiHarness.TermsVersion }, Ct);
        var wrongPassword = await anonymous.PostAsJsonAsync(url, new { email, password = "Otra-Clave-99" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, missingBody.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, withoutCode.StatusCode);
        Assert.Equal("identity.credentials.invalid", await WholesaleApiHarness.CodeOfAsync(withoutCode));
        Assert.Equal(0, usersAfterNoCode);
        Assert.Equal(HttpStatusCode.Unauthorized, fakeCodeWithoutTerms.StatusCode);
        Assert.Equal("identity.credentials.invalid", await WholesaleApiHarness.CodeOfAsync(fakeCodeWithoutTerms));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = (await response.Content.ReadFromJsonAsync<SessionResponse>(Ct))!;
        Assert.Equal(email, session.Email);
        Assert.Empty(session.ActiveTenantIds);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any());
        var terms = await WholesaleApiHarness.RowsAsync<string>(
            Factory, $"""SELECT version AS "Value" FROM identity.terms_acceptances WHERE user_id = {session.UserId}""");
        Assert.Equal([WholesaleApiHarness.TermsVersion], terms);
        // Con contraseña ya creada, una distinta no abre sesión.
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal("identity.credentials.invalid", await WholesaleApiHarness.CodeOfAsync(wrongPassword));

        // Los términos ya quedaron: crear la empresa no los vuelve a pedir.
        var accepted = await AcceptNewTenantAsync(session.UserId, token);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    // Spec 2026-11-24: quien llega desde el correo ya probó el buzón: crea la cuenta sin código, con los términos registrados.
    // La prueba se lee del outbox, así que esta prueba fija también el nombre del campo del evento.
    [Fact]
    public async Task TheEmailLinkProofCreatesTheAccountWithoutACode()
    {
        var email = NewEmail();
        var (_, distributor, token) = await WholesaleApiHarness.InvitedAsync(Factory, email);
        var emailProof = await WholesaleApiHarness.LatestEmailProofAsync(Factory, distributor.Id);
        using var anonymous = WholesaleApiHarness.CreateAnonymous(Factory);

        var response = await anonymous.PostAsJsonAsync(
            $"{WholesaleApiHarness.InvitationUrl(token)}/session-with-password",
            new { email, password = Password, termsVersion = WholesaleApiHarness.TermsVersion, emailProof },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = (await response.Content.ReadFromJsonAsync<SessionResponse>(Ct))!;
        Assert.Equal(email, session.Email);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any());
        var terms = await WholesaleApiHarness.RowsAsync<string>(
            Factory, $"""SELECT version AS "Value" FROM identity.terms_acceptances WHERE user_id = {session.UserId}""");
        Assert.Equal([WholesaleApiHarness.TermsVersion], terms);
        // La prueba cuenta como un código válido: la cuenta nace con el correo verificado, sin que se emitiera ningún código.
        var verified = await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""
            SELECT count(*)::int AS "Value" FROM identity.users
            WHERE id = {session.UserId} AND email = {email} AND email_verified_at IS NOT NULL
            """);
        Assert.Equal(1, verified[0]);
        Assert.Null(await WholesaleApiHarness.LatestEmailCodeAsync(Factory, email));
        Assert.Equal(HttpStatusCode.OK, (await AcceptNewTenantAsync(session.UserId, token)).StatusCode);
    }

    // La prueba sólo vale para el correo invitado, y una prueba errada responde lo mismo que un código errado.
    [Fact]
    public async Task AnEmailProofWithAnotherEmailOrAWrongProofIs401AndCreatesNoUser()
    {
        var email = NewEmail();
        var otherEmail = NewEmail();
        var (_, distributor, token) = await WholesaleApiHarness.InvitedAsync(Factory, email);
        var emailProof = await WholesaleApiHarness.LatestEmailProofAsync(Factory, distributor.Id);
        Assert.NotNull(emailProof);
        using var anonymous = WholesaleApiHarness.CreateAnonymous(Factory);
        var url = $"{WholesaleApiHarness.InvitationUrl(token)}/session-with-password";

        var anotherEmail = await anonymous.PostAsJsonAsync(
            url,
            new { email = otherEmail, password = Password, termsVersion = WholesaleApiHarness.TermsVersion, emailProof },
            Ct);
        var wrongProof = await anonymous.PostAsJsonAsync(
            url,
            new { email, password = Password, termsVersion = WholesaleApiHarness.TermsVersion, emailProof = "prueba-falsa" },
            Ct);

        Assert.Equal(
            (HttpStatusCode.Unauthorized, "identity.credentials.invalid"),
            (anotherEmail.StatusCode, await WholesaleApiHarness.CodeOfAsync(anotherEmail)));
        Assert.Equal(
            (HttpStatusCode.Unauthorized, "identity.credentials.invalid"),
            (wrongProof.StatusCode, await WholesaleApiHarness.CodeOfAsync(wrongProof)));
        Assert.Equal(0, await CountUsersAsync(otherEmail));
        Assert.Equal(0, await CountUsersAsync(email));
    }

    [Fact]
    public async Task TermsMissingOrStaleWithAValidProofAre422AndCreateNoUser()
    {
        var email = NewEmail();
        var (_, distributor, token) = await WholesaleApiHarness.InvitedAsync(Factory, email);
        var emailProof = await WholesaleApiHarness.LatestEmailProofAsync(Factory, distributor.Id);
        Assert.NotNull(emailProof);
        using var anonymous = WholesaleApiHarness.CreateAnonymous(Factory);
        var url = $"{WholesaleApiHarness.InvitationUrl(token)}/session-with-password";

        var missing = await anonymous.PostAsJsonAsync(url, new { email, password = Password, emailProof }, Ct);
        var stale = await anonymous.PostAsJsonAsync(
            url, new { email, password = Password, termsVersion = "2025-01-01", emailProof }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Contains("\"TermsVersion\"", await WholesaleApiHarness.BodyAsync(missing), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, stale.StatusCode);
        Assert.Equal("identity.terms.version_mismatch", await WholesaleApiHarness.CodeOfAsync(stale));
        Assert.Equal(0, await CountUsersAsync(email));
    }

    // Anti-objetivo del spec 2026-11-24: con la contraseña ya creada, la prueba no la cambia ni deja entrar con otra.
    [Fact]
    public async Task AProofDoesNotChangeThePasswordOfAnExistingAccount()
    {
        var email = NewEmail();
        var (_, distributor, token) = await WholesaleApiHarness.InvitedAsync(Factory, email);
        var emailProof = await WholesaleApiHarness.LatestEmailProofAsync(Factory, distributor.Id);
        Assert.NotNull(emailProof);
        var userId = await WholesaleApiHarness.PasswordSessionAsync(Factory, token, Password);
        using var anonymous = WholesaleApiHarness.CreateAnonymous(Factory);
        var url = $"{WholesaleApiHarness.InvitationUrl(token)}/session-with-password";

        var otherPassword = await anonymous.PostAsJsonAsync(
            url,
            new { email, password = "Otra-Clave-99", termsVersion = WholesaleApiHarness.TermsVersion, emailProof },
            Ct);
        var original = await anonymous.PostAsJsonAsync(url, new { email, password = Password }, Ct);

        Assert.Equal(
            (HttpStatusCode.Unauthorized, "identity.credentials.invalid"),
            (otherPassword.StatusCode, await WholesaleApiHarness.CodeOfAsync(otherPassword)));
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        Assert.Equal(userId, (await original.Content.ReadFromJsonAsync<SessionResponse>(Ct))!.UserId);
    }

    // Revisión del bloque C: con el correo en el cuerpo, la respuesta sin código no puede decir si un correo cualquiera tiene
    // contraseña. Un correo sin cuenta y una cuenta con contraseña y una clave errada responden igual.
    [Fact]
    public async Task SessionWithPasswordDoesNotTellWhetherAnEmailHasAPassword()
    {
        var withPassword = NewEmail();
        var (_, _, token) = await WholesaleApiHarness.InvitedAsync(Factory, NewEmail());
        await WholesaleApiHarness.PasswordSessionAsync(Factory, token, Password, withPassword);
        using var anonymous = WholesaleApiHarness.CreateAnonymous(Factory);
        var url = $"{WholesaleApiHarness.InvitationUrl(token)}/session-with-password";

        var unknown = await anonymous.PostAsJsonAsync(url, new { email = NewEmail(), password = Password }, Ct);
        var wrongPassword = await anonymous.PostAsJsonAsync(url, new { email = withPassword, password = "Otra-Clave-99" }, Ct);

        Assert.Equal(
            (HttpStatusCode.Unauthorized, "identity.credentials.invalid"),
            (unknown.StatusCode, await WholesaleApiHarness.CodeOfAsync(unknown)));
        Assert.Equal(
            (unknown.StatusCode, await WholesaleApiHarness.CodeOfAsync(unknown)),
            (wrongPassword.StatusCode, await WholesaleApiHarness.CodeOfAsync(wrongPassword)));
    }

    // Revisión del bloque C: las tres rutas anónimas que tocan credenciales llevan el limitador Credentials (10 por minuto por IP
    // y por ruta). El harness da una IP por cliente, así que se mira la metadata del endpoint, no un 429.
    [Fact]
    public void TheAnonymousCredentialRoutesCarryTheCredentialsRateLimit()
    {
        var policies = Factory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v1/wholesale-invitations/", StringComparison.Ordinal) == true)
            .GroupBy(endpoint => endpoint.RoutePattern.RawText!)
            .ToDictionary(
                group => group.Key,
                group => group.Select(endpoint =>
                    endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>()?.PolicyName).ToList());

        foreach (var route in new[] { "email-code", "session", "session-with-password" })
        {
            Assert.Equal([RateLimiterPolicies.Credentials], policies[$"/api/v1/wholesale-invitations/{{token}}/{route}"]);
        }
    }

    // Spec 2026-11-19: Google abre la sesión del correo verificado, sea o no el invitado; sin verificar no entra.
    [Fact]
    public async Task AGoogleSessionNeedsAVerifiedEmailButNotTheInvitedOne()
    {
        var email = NewEmail();
        var otherEmail = NewEmail();
        var (_, _, token) = await WholesaleApiHarness.InvitedAsync(Factory, email);
        using var other = WholesaleApiHarness.CreateGoogle(Factory, otherEmail);
        using var unverified = WholesaleApiHarness.CreateGoogle(Factory, NewEmail(), verified: false);
        using var invited = WholesaleApiHarness.CreateGoogle(Factory, $"  {email.ToUpperInvariant()} ");

        var otherSession = await other.PostAsync($"{WholesaleApiHarness.InvitationUrl(token)}/session", null, Ct);
        var notVerified = await unverified.PostAsync($"{WholesaleApiHarness.InvitationUrl(token)}/session", null, Ct);
        var ok = await invited.PostAsync($"{WholesaleApiHarness.InvitationUrl(token)}/session", null, Ct);

        Assert.Equal(HttpStatusCode.OK, otherSession.StatusCode);
        Assert.Equal(otherEmail, (await otherSession.Content.ReadFromJsonAsync<SessionResponse>(Ct))!.Email);
        Assert.Equal(HttpStatusCode.Forbidden, notVerified.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(email, (await ok.Content.ReadFromJsonAsync<SessionResponse>(Ct))!.Email);
    }

    // D-21 y Review Focus: el alta del distribuidor no lee Registration:PublicTenantSignupEnabled. Host propio: cambia la
    // configuración.
    [Fact]
    public async Task NewTenantCreatesOnlyCatalogSuppliersAndPurchasesEvenWithSignupDisabled()
    {
        await using var database = await WholesaleApiHarness.StartDatabaseAsync();
        await using var factory = new WholesaleApiHarness.QepApiFactory(
            database.GetConnectionString(),
            settings: new Dictionary<string, string> { ["Registration:PublicTenantSignupEnabled"] = "false" });
        var (_, _, token) = await WholesaleApiHarness.InvitedAsync(factory, NewEmail());
        var userId = await WholesaleApiHarness.PasswordSessionAsync(factory, token, Password);
        using var session = WholesaleApiHarness.CreateSession(factory, userId);

        var response = await session.PostAsJsonAsync(
            $"{WholesaleApiHarness.InvitationUrl(token)}/accept", new { mode = "NewTenant" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<AcceptedInvitationDto>(Ct))!;
        Assert.True(accepted.Created);
        var modules = await WholesaleApiHarness.RowsAsync<string>(
            factory,
            $"""
            SELECT module_key AS "Value" FROM tenancy.tenant_modules WHERE tenant_id = {accepted.TenantId} ORDER BY module_key
            """);
        Assert.Equal(
            ["catalog", "purchases", "suppliers"],
            modules);
        Assert.DoesNotContain(TenantModuleKeys.Shopify.Value, modules);
        var owner = await WholesaleApiHarness.RowsAsync<int>(
            factory,
            $"""
            SELECT count(*)::int AS "Value" FROM tenancy.tenants t
            JOIN tenancy.memberships m ON m.id = t.owner_membership_id AND m.tenant_id = t.id
            WHERE t.id = {accepted.TenantId} AND m.user_id = {userId}
            """);
        Assert.Equal(1, owner[0]);
    }

    // Spec 2026-11-22: el tenant nuevo nace con el país del distribuidor guardado; un `country` en el cuerpo del accept se ignora.
    [Fact]
    public async Task NewTenantIsBornWithTheDistributorCountryAndNotTheBodyOne()
    {
        var (_, _, token) = await WholesaleApiHarness.InvitedAsync(
            Factory, NewEmail(), "B12345678", country: "ES", identificationType: "NIF");
        var userId = await WholesaleApiHarness.PasswordSessionAsync(Factory, token, Password);
        using var session = WholesaleApiHarness.CreateSession(Factory, userId);

        var response = await session.PostAsJsonAsync(
            $"{WholesaleApiHarness.InvitationUrl(token)}/accept", new { mode = "NewTenant", country = "FR" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<AcceptedInvitationDto>(Ct))!;
        Assert.True(accepted.Created);
        Assert.Equal("ES", await TenantCountryAsync(accepted.TenantId));
    }

    [Fact]
    public async Task NewTenantOfAColombianDistributorStaysInColombia()
    {
        var (_, _, token) = await WholesaleApiHarness.InvitedAsync(Factory, NewEmail());
        var userId = await WholesaleApiHarness.PasswordSessionAsync(Factory, token, Password);

        var accepted = (await (await AcceptNewTenantAsync(userId, token)).Content.ReadFromJsonAsync<AcceptedInvitationDto>(Ct))!;

        Assert.Equal("CO", await TenantCountryAsync(accepted.TenantId));
    }

    // Spec 2026-11-22: vincular no sobrescribe el país del tenant que el distribuidor ya tenía.
    [Fact]
    public async Task LinkTenantDoesNotChangeTheCountryOfTheLinkedTenant()
    {
        var mine = await WholesaleApiHarness.NewTenantAsync(Factory);
        var (_, _, token) = await WholesaleApiHarness.InvitedAsync(
            Factory, WholesaleApiHarness.EmailOf(mine), "B12345678", country: "ES", identificationType: "NIF");
        using var session = WholesaleApiHarness.CreateSession(Factory, mine.SubjectId, mine.TenantId);

        var response = await session.PostAsJsonAsync(
            $"{WholesaleApiHarness.InvitationUrl(token)}/accept", new { mode = "LinkTenant", tenantId = mine.TenantId }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("CO", await TenantCountryAsync(mine.TenantId));
    }

    // Owner, 2026-11-24: al vincular un tenant que ya existía, la empresa del distribuidor nace sólo si ese tenant no tiene ninguna.
    [Fact]
    public async Task LinkTenantOnATenantWithoutCompaniesCreatesTheDistributorCompany()
    {
        var mine = await WholesaleApiHarness.NewTenantAsync(Factory);
        var (_, _, token) = await WholesaleApiHarness.InvitedAsync(Factory, WholesaleApiHarness.EmailOf(mine));
        using var session = WholesaleApiHarness.CreateSession(Factory, mine.SubjectId, mine.TenantId);

        var response = await session.PostAsJsonAsync(
            $"{WholesaleApiHarness.InvitationUrl(token)}/accept", new { mode = "LinkTenant", tenantId = mine.TenantId }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var companies = await WholesaleApiHarness.RowsAsync<int>(
            Factory, $"""SELECT count(*)::int AS "Value" FROM companies.companies WHERE tenant_id = {mine.TenantId}""");
        Assert.Equal(1, companies[0]);
    }

    [Fact]
    public async Task LinkTenantOnATenantThatAlreadyHasACompanyCreatesNone()
    {
        var mine = await WholesaleApiHarness.NewTenantAsync(Factory);
        using (var manager = WholesaleApiHarness.CreateManager(Factory, mine))
        {
            await WholesaleApiHarness.CreateCompanyAsync(manager, mine.TenantId, "La que ya tenia", "800111222");
        }

        var (_, _, token) = await WholesaleApiHarness.InvitedAsync(Factory, WholesaleApiHarness.EmailOf(mine));
        using var session = WholesaleApiHarness.CreateSession(Factory, mine.SubjectId, mine.TenantId);

        var response = await session.PostAsJsonAsync(
            $"{WholesaleApiHarness.InvitationUrl(token)}/accept", new { mode = "LinkTenant", tenantId = mine.TenantId }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var companies = await WholesaleApiHarness.RowsAsync<int>(
            Factory, $"""SELECT count(*)::int AS "Value" FROM companies.companies WHERE tenant_id = {mine.TenantId}""");
        Assert.Equal(1, companies[0]);
    }

    private async Task<string> TenantCountryAsync(Guid tenantId) =>
        (await WholesaleApiHarness.RowsAsync<string>(
            Factory, $"""SELECT country AS "Value" FROM tenancy.tenants WHERE id = {tenantId}"""))[0];

    // Spec 2026-11-19 §3b: el formulario llega prellenado con la razón social y un slug sin la forma societaria del final.
    [Fact]
    public async Task TheSummarySuggestsTheSlugWithoutTheCorporateSuffix()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (_, _, token) = await WholesaleApiHarness.InvitedAsync(
            Factory, NewEmail(), businessName: $"Distribuciones Andina {suffix} S.A.S.");
        using var anonymous = WholesaleApiHarness.CreateAnonymous(Factory);

        var summary = await anonymous.GetFromJsonAsync<WholesaleInvitationResponse>(WholesaleApiHarness.InvitationUrl(token), Ct);

        Assert.Equal($"distribuciones-andina-{suffix}", summary!.SuggestedSlug);
        Assert.Equal($"Distribuciones Andina {suffix} S.A.S.", summary.BusinessName);
    }

    // El frontend no puede adivinar la versión vigente de los términos: la lee de aquí y la devuelve al crear la cuenta o el
    // tenant. Sin versión configurada viaja nula (la pantalla no deja crear cuenta nueva: el backend falla cerrado).
    [Fact]
    public async Task TheSummaryCarriesTheCurrentTermsVersion()
    {
        var (_, _, token) = await WholesaleApiHarness.InvitedAsync(Factory, NewEmail());
        using var anonymous = WholesaleApiHarness.CreateAnonymous(Factory);

        var summary = await anonymous.GetFromJsonAsync<WholesaleInvitationResponse>(WholesaleApiHarness.InvitationUrl(token), Ct);

        Assert.Equal(WholesaleApiHarness.TermsVersion, summary!.TermsVersion);
    }

    [Fact]
    public async Task WithoutAConfiguredTermsVersionTheSummaryCarriesNone()
    {
        await using var database = await WholesaleApiHarness.StartDatabaseAsync();
        await using var factory = new WholesaleApiHarness.QepApiFactory(
            database.GetConnectionString(), settings: new Dictionary<string, string> { ["Terms:CurrentVersion"] = "  " });
        var (_, _, token) = await WholesaleApiHarness.InvitedAsync(factory, NewEmail());
        using var anonymous = WholesaleApiHarness.CreateAnonymous(factory);

        var summary = await anonymous.GetFromJsonAsync<WholesaleInvitationResponse>(WholesaleApiHarness.InvitationUrl(token), Ct);

        Assert.Null(summary!.TermsVersion);
    }

    // Sin nombre ni slug en el cuerpo (un cliente sin edición): la razón social y el slug que sugiere el servidor.
    [Fact]
    public async Task ANewTenantWithoutNameOrSlugUsesTheBusinessNameAndASuggestedSlug()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var businessName = $"Distribución Ñandú {suffix} S.A.S.";
        var (_, _, token) = await WholesaleApiHarness.InvitedAsync(Factory, NewEmail(), businessName: businessName);
        var userId = await WholesaleApiHarness.PasswordSessionAsync(Factory, token, Password);
        using var session = WholesaleApiHarness.CreateSession(Factory, userId);

        var response = await session.PostAsJsonAsync(
            $"{WholesaleApiHarness.InvitationUrl(token)}/accept", new { mode = "NewTenant" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<AcceptedInvitationDto>(Ct))!;
        Assert.Equal(businessName, accepted.TenantName);
        var tenant = await WholesaleApiHarness.RowsAsync<string>(
            Factory,
            $"""SELECT slug || '|' || display_name || '|' || default_culture || '|' || time_zone AS "Value" FROM tenancy.tenants WHERE id = {accepted.TenantId}""");
        Assert.Equal([$"distribucion-nandu-{suffix}|{businessName}|es-CO|America/Bogota"], tenant);
    }

    // El distribuidor puede corregir lo que vino prellenado: lo que manda en el cuerpo es lo que se crea.
    [Fact]
    public async Task ANewTenantUsesTheNameAndSlugTheDistributorEdited()
    {
        var (_, _, token) = await WholesaleApiHarness.InvitedAsync(Factory, NewEmail(), businessName: "Razón Social Original");
        var userId = await WholesaleApiHarness.PasswordSessionAsync(Factory, token, Password);
        var slug = NewSlug();
        using var session = WholesaleApiHarness.CreateSession(Factory, userId);

        var response = await session.PostAsJsonAsync(
            $"{WholesaleApiHarness.InvitationUrl(token)}/accept",
            new { mode = "NewTenant", displayName = "  Mi Empresa Corregida  ", slug },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<AcceptedInvitationDto>(Ct))!;
        Assert.Equal("Mi Empresa Corregida", accepted.TenantName);
        var tenant = await WholesaleApiHarness.RowsAsync<string>(
            Factory,
            $"""SELECT slug || '|' || display_name AS "Value" FROM tenancy.tenants WHERE id = {accepted.TenantId}""");
        Assert.Equal([$"{slug}|Mi Empresa Corregida"], tenant);
    }

    // Un slug que el distribuidor escribió y ya está tomado no se reemplaza solo: es un 422 con su código, para que lo corrija,
    // y la invitación sigue pendiente.
    [Fact]
    public async Task ATakenEditedSlugIs422AndTheInvitationStaysPending()
    {
        var (_, _, firstToken) = await WholesaleApiHarness.InvitedAsync(Factory, NewEmail());
        var firstUser = await WholesaleApiHarness.PasswordSessionAsync(Factory, firstToken, Password);
        var slug = NewSlug();
        using var firstSession = WholesaleApiHarness.CreateSession(Factory, firstUser);
        (await firstSession.PostAsJsonAsync(
            $"{WholesaleApiHarness.InvitationUrl(firstToken)}/accept", new { mode = "NewTenant", slug }, Ct)).EnsureSuccessStatusCode();
        var (_, _, token) = await WholesaleApiHarness.InvitedAsync(Factory, NewEmail());
        var userId = await WholesaleApiHarness.PasswordSessionAsync(Factory, token, Password);
        using var session = WholesaleApiHarness.CreateSession(Factory, userId);

        var response = await session.PostAsJsonAsync(
            $"{WholesaleApiHarness.InvitationUrl(token)}/accept", new { mode = "NewTenant", slug }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("tenancy.slug.taken", await response.Content.ReadAsStringAsync(Ct));
        using var anonymous = WholesaleApiHarness.CreateAnonymous(Factory);
        var summary = await anonymous.GetFromJsonAsync<WholesaleInvitationResponse>(WholesaleApiHarness.InvitationUrl(token), Ct);
        Assert.Equal("pending", summary!.Status);
    }

    [Fact]
    public async Task NewTenantCreatesTheLinkedSupplierInTheDistributorAndTheCustomerInTheWholesaler()
    {
        var (wholesaler, distributor, token) = await WholesaleApiHarness.InvitedAsync(Factory, NewEmail());
        var userId = await WholesaleApiHarness.PasswordSessionAsync(Factory, token, Password);

        var response = await AcceptNewTenantAsync(userId, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<AcceptedInvitationDto>(Ct))!;
        Assert.Equal((WholesaleApiHarness.DefaultBusinessName, true), (accepted.TenantName, accepted.Created));
        var suppliers = await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""
            SELECT count(*)::int AS "Value" FROM suppliers.suppliers
            WHERE tenant_id = {accepted.TenantId} AND linked_tenant_id = {wholesaler.TenantId}
            """);
        Assert.Equal(1, suppliers[0]);
        var customers = await WholesaleApiHarness.RowsAsync<Guid>(
            Factory,
            $"""
            SELECT id AS "Value" FROM customers.customers
            WHERE tenant_id = {wholesaler.TenantId} AND identification_number = '900123456'
            """);
        var customer = Assert.Single(customers);

        using var manager = WholesaleApiHarness.CreateManager(Factory, wholesaler);
        var detail = (await manager.GetFromJsonAsync<DistributorDetailDto>(
            $"{WholesaleApiHarness.DistributorsUrl(wholesaler.TenantId)}/{distributor.Id}", Ct))!;
        Assert.Equal(("Active", accepted.TenantId, (Guid?)customer), (detail.Status, detail.DistributorTenant!.Id, detail.CustomerId));

        // D-16 y ruling W9: repetirlo es la misma respuesta, sin crear nada.
        var again = (await (await AcceptNewTenantAsync(userId, token)).Content.ReadFromJsonAsync<AcceptedInvitationDto>(Ct))!;
        Assert.Equal((accepted.TenantId, false), (again.TenantId, again.Created));
        Assert.Empty(again.EnabledModules);
    }

    [Fact]
    public async Task LinkTenantEnablesSuppliersAndPurchasesAndKeepsTheRest()
    {
        var mine = await WholesaleApiHarness.NewTenantAsync(Factory, [TenantModuleKeys.Catalog, TenantModuleKeys.Customers]);
        var (wholesaler, _, token) = await WholesaleApiHarness.InvitedAsync(Factory, WholesaleApiHarness.EmailOf(mine));
        using var session = WholesaleApiHarness.CreateSession(Factory, mine.SubjectId, mine.TenantId);

        var linkable = await session.GetFromJsonAsync<LinkableTenantsResponse>(
            $"{WholesaleApiHarness.InvitationUrl(token)}/linkable-tenants", Ct);
        var response = await session.PostAsJsonAsync(
            $"{WholesaleApiHarness.InvitationUrl(token)}/accept", new { mode = "LinkTenant", tenantId = mine.TenantId }, Ct);

        var option = Assert.Single(linkable!.Tenants);
        Assert.Equal((mine.TenantId, true, (string?)null), (option.TenantId, option.Linkable, option.IneligibleReason));
        Assert.Equal(["suppliers", "purchases"], option.ModulesToEnable);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<AcceptedInvitationDto>(Ct))!;
        Assert.Equal((mine.TenantId, false), (accepted.TenantId, accepted.Created));
        Assert.Equal(["suppliers", "purchases"], accepted.EnabledModules);
        var modules = await WholesaleApiHarness.RowsAsync<string>(
            Factory,
            $"""
            SELECT module_key AS "Value" FROM tenancy.tenant_modules WHERE tenant_id = {mine.TenantId} ORDER BY module_key
            """);
        Assert.Equal(["catalog", "customers", "purchases", "suppliers"], modules);
        var supplier = await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""
            SELECT count(*)::int AS "Value" FROM suppliers.suppliers
            WHERE tenant_id = {mine.TenantId} AND linked_tenant_id = {wholesaler.TenantId}
            """);
        Assert.Equal(1, supplier[0]);
    }

    // Spec 2026-11-19 §6: con el mismo correo no se escribe nada del correo, ni auditoría ni aviso.
    [Fact]
    public async Task AnExistingAccountLinksWithoutTouchingAnythingIfTheEmailMatches()
    {
        var mine = await WholesaleApiHarness.NewTenantAsync(Factory);
        var email = WholesaleApiHarness.EmailOf(mine);
        var (wholesaler, distributor, token) = await WholesaleApiHarness.InvitedAsync(Factory, email);
        using var session = WholesaleApiHarness.CreateSession(Factory, mine.SubjectId, mine.TenantId);

        var response = await session.PostAsJsonAsync(
            $"{WholesaleApiHarness.InvitationUrl(token)}/accept", new { mode = "LinkTenant", tenantId = mine.TenantId }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var manager = WholesaleApiHarness.CreateManager(Factory, wholesaler);
        var detail = (await manager.GetFromJsonAsync<DistributorDetailDto>(
            $"{WholesaleApiHarness.DistributorsUrl(wholesaler.TenantId)}/{distributor.Id}", Ct))!;
        Assert.Equal(("Active", email), (detail.Status, detail.Email));
        Assert.Equal(0, await EmailChangesAsync(wholesaler.TenantId, distributor.Id));
    }

    // Decisión del owner (2026-11-19): prevalece el correo de la cuenta, sin aprobación y con aviso a las dos partes.
    [Fact]
    public async Task ADifferentEmailReplacesTheWholesalersRecordAndNotifiesBoth()
    {
        var invitedEmail = NewEmail();
        var accountEmail = NewEmail();
        var (wholesaler, distributor, token) = await WholesaleApiHarness.InvitedAsync(Factory, invitedEmail);
        var userId = await WholesaleApiHarness.PasswordSessionAsync(Factory, token, Password, accountEmail);

        var response = await AcceptNewTenantAsync(userId, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var manager = WholesaleApiHarness.CreateManager(Factory, wholesaler);
        var detail = (await manager.GetFromJsonAsync<DistributorDetailDto>(
            $"{WholesaleApiHarness.DistributorsUrl(wholesaler.TenantId)}/{distributor.Id}", Ct))!;
        Assert.Equal(accountEmail, detail.Email);
        // La auditoría llega por outbox: se espera a que aparezca.
        Assert.Equal(
            2,
            await WholesaleApiHarness.EventuallyAsync(
                () => EmailChangesAsync(wholesaler.TenantId, distributor.Id), count => count >= 2));
        var notice = await WholesaleApiHarness.RowsAsync<string>(
            Factory,
            $"""
            SELECT (payload ->> 'previousEmail') || '>' || (payload ->> 'newEmail') AS "Value" FROM platform.outbox_messages
            WHERE event_name = {EmailChangedEvent} AND payload ->> 'distributorId' = {distributor.Id.ToString()}
            """);
        Assert.Equal([$"{invitedEmail}>{accountEmail}"], notice);
    }

    // Spec 2026-11-19 §5 y §7: billing no administra el tenant, así que no lo puede vincular (el código de siempre); sí puede
    // crear una empresa nueva con su sesión, aceptando los términos.
    [Fact]
    public async Task ABillingMemberIsToldToChangeOrganizationAndCanCreateANewTenant()
    {
        var company = await WholesaleApiHarness.NewTenantAsync(Factory);
        var billing = await WholesaleApiHarness.AddMemberAsync(Factory, company.TenantId, "billing");
        var (_, _, token) = await WholesaleApiHarness.InvitedAsync(Factory, $"miembro-{billing:N}@example.com");
        using var session = WholesaleApiHarness.CreateSession(Factory, billing, company.TenantId);
        var url = WholesaleApiHarness.InvitationUrl(token);

        var linkable = await session.GetFromJsonAsync<LinkableTenantsResponse>($"{url}/linkable-tenants", Ct);
        var link = await session.PostAsJsonAsync($"{url}/accept", new { mode = "LinkTenant", tenantId = company.TenantId }, Ct);
        var withoutTerms = await session.PostAsJsonAsync($"{url}/accept", new { mode = "NewTenant" }, Ct);
        var created = await session.PostAsJsonAsync(
            $"{url}/accept", new { mode = "NewTenant", termsVersion = WholesaleApiHarness.TermsVersion }, Ct);

        Assert.Empty(linkable!.Tenants);
        Assert.Equal(HttpStatusCode.Forbidden, link.StatusCode);
        Assert.Equal("wholesale.invitation.not_tenant_owner", await WholesaleApiHarness.CodeOfAsync(link));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, withoutTerms.StatusCode);
        Assert.Contains("\"TermsVersion\"", await WholesaleApiHarness.BodyAsync(withoutTerms), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.True((await created.Content.ReadFromJsonAsync<AcceptedInvitationDto>(Ct))!.Created);
        var terms = await WholesaleApiHarness.RowsAsync<int>(
            Factory, $"""SELECT count(*)::int AS "Value" FROM identity.terms_acceptances WHERE user_id = {billing}""");
        Assert.Equal(1, terms[0]);
    }

    [Fact]
    public async Task AnAdminLinksTheTenant()
    {
        var company = await WholesaleApiHarness.NewTenantAsync(Factory);
        var admin = await WholesaleApiHarness.AddMemberAsync(Factory, company.TenantId, "admin");
        var (wholesaler, _, token) = await WholesaleApiHarness.InvitedAsync(Factory, $"miembro-{admin:N}@example.com");
        using var session = WholesaleApiHarness.CreateSession(Factory, admin, company.TenantId);

        var linkable = await session.GetFromJsonAsync<LinkableTenantsResponse>(
            $"{WholesaleApiHarness.InvitationUrl(token)}/linkable-tenants", Ct);
        var response = await session.PostAsJsonAsync(
            $"{WholesaleApiHarness.InvitationUrl(token)}/accept", new { mode = "LinkTenant", tenantId = company.TenantId }, Ct);

        Assert.Equal(company.TenantId, Assert.Single(linkable!.Tenants).TenantId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var suppliers = await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""
            SELECT count(*)::int AS "Value" FROM suppliers.suppliers
            WHERE tenant_id = {company.TenantId} AND linked_tenant_id = {wholesaler.TenantId}
            """);
        Assert.Equal(1, suppliers[0]);
    }

    // Spec 2026-11-19: el link identifica la invitación, no el correo. Cualquier cuenta la acepta, una sola vez.
    [Fact]
    public async Task AnotherAccountAcceptsAndTheInvitationIsConsumedOnlyOnce()
    {
        var (wholesaler, distributor, token) = await WholesaleApiHarness.InvitedAsync(Factory, NewEmail());
        var first = await WholesaleApiHarness.NewTenantAsync(Factory);
        var second = await WholesaleApiHarness.NewTenantAsync(Factory);
        using var firstSession = WholesaleApiHarness.CreateSession(Factory, first.SubjectId, first.TenantId);
        using var secondSession = WholesaleApiHarness.CreateSession(Factory, second.SubjectId, second.TenantId);
        var url = $"{WholesaleApiHarness.InvitationUrl(token)}/accept";
        var body = new { mode = "NewTenant", termsVersion = WholesaleApiHarness.TermsVersion };

        var accepted = await firstSession.PostAsJsonAsync(url, body, Ct);
        var again = await firstSession.PostAsJsonAsync(url, body, Ct);
        // Sin términos: la invitación ya consumida responde su propio 409 antes de pedir términos o registrarlos.
        var refused = await secondSession.PostAsJsonAsync(url, new { mode = "NewTenant" }, Ct);
        var linkable = await secondSession.GetAsync($"{WholesaleApiHarness.InvitationUrl(token)}/linkable-tenants", Ct);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var tenantId = (await accepted.Content.ReadFromJsonAsync<AcceptedInvitationDto>(Ct))!.TenantId;
        Assert.Equal(tenantId, (await again.Content.ReadFromJsonAsync<AcceptedInvitationDto>(Ct))!.TenantId);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("wholesale.invitation.already_accepted", await WholesaleApiHarness.CodeOfAsync(refused));
        Assert.Equal(HttpStatusCode.Conflict, linkable.StatusCode);
        using var manager = WholesaleApiHarness.CreateManager(Factory, wholesaler);
        var detail = (await manager.GetFromJsonAsync<DistributorDetailDto>(
            $"{WholesaleApiHarness.DistributorsUrl(wholesaler.TenantId)}/{distributor.Id}", Ct))!;
        Assert.Equal(("Active", tenantId, WholesaleApiHarness.EmailOf(first)), (detail.Status, detail.DistributorTenant!.Id, detail.Email));
        var secondTenants = await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""
            SELECT count(*)::int AS "Value" FROM tenancy.memberships WHERE user_id = {second.SubjectId}
            """);
        Assert.Equal(1, secondTenants[0]);
        var secondTerms = await WholesaleApiHarness.RowsAsync<int>(
            Factory, $"""SELECT count(*)::int AS "Value" FROM identity.terms_acceptances WHERE user_id = {second.SubjectId}""");
        Assert.Equal(0, secondTerms[0]);
    }

    [Fact]
    public async Task AnAcceptWithoutModeIs422WithTheField()
    {
        var (_, _, token) = await WholesaleApiHarness.InvitedAsync(Factory, NewEmail());
        var userId = await WholesaleApiHarness.PasswordSessionAsync(Factory, token, Password);
        using var session = WholesaleApiHarness.CreateSession(Factory, userId);

        var noMode = await session.PostAsJsonAsync($"{WholesaleApiHarness.InvitationUrl(token)}/accept", new { }, Ct);
        var noTenant = await session.PostAsJsonAsync(
            $"{WholesaleApiHarness.InvitationUrl(token)}/accept", new { mode = "LinkTenant" }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, noMode.StatusCode);
        Assert.Contains("\"Mode\"", await WholesaleApiHarness.BodyAsync(noMode), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noTenant.StatusCode);
        Assert.Contains("\"TenantId\"", await WholesaleApiHarness.BodyAsync(noTenant), StringComparison.Ordinal);
    }

    // Review Focus 1: doble clic o dos pestañas. Un tenant, un proveedor, un cliente, y las dos respuestas sobre el mismo tenant.
    [Fact]
    public async Task TwoConcurrentAcceptsCreateOneTenantOneSupplierOneCustomer()
    {
        var businessName = $"Doble Clic {Guid.NewGuid():N}"[..24];
        var (wholesaler, _, token) = await WholesaleApiHarness.InvitedAsync(Factory, NewEmail(), "901222333", businessName);
        var userId = await WholesaleApiHarness.PasswordSessionAsync(Factory, token, Password);

        var responses = await Task.WhenAll(AcceptNewTenantAsync(userId, token), AcceptNewTenantAsync(userId, token));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var accepted = await Task.WhenAll(responses.Select(response => response.Content.ReadFromJsonAsync<AcceptedInvitationDto>(Ct)));
        Assert.Equal(accepted[0]!.TenantId, accepted[1]!.TenantId);
        var tenants = await WholesaleApiHarness.RowsAsync<int>(
            Factory, $"""SELECT count(*)::int AS "Value" FROM tenancy.tenants WHERE display_name = {businessName}""");
        var suppliers = await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""
            SELECT count(*)::int AS "Value" FROM suppliers.suppliers
            WHERE tenant_id = {accepted[0]!.TenantId} AND linked_tenant_id = {wholesaler.TenantId}
            """);
        var customers = await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""
            SELECT count(*)::int AS "Value" FROM customers.customers
            WHERE tenant_id = {wholesaler.TenantId} AND identification_number = '901222333'
            """);
        // Spec 2026-11-23 §2: el candado por tenant deja una sola empresa aunque las dos pestañas lleguen juntas al paso.
        var companies = await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""SELECT count(*)::int AS "Value" FROM companies.companies WHERE tenant_id = {accepted[0]!.TenantId}""");
        Assert.Equal((1, 1, 1, 1), (tenants[0], suppliers[0], customers[0], companies[0]));
    }

    // Spec 2026-11-23 §3: la empresa nace en el tenant nuevo con lo que escribió el mayorista, y el IVA colombiano se siembra
    // (cubre el riesgo del QuotationsDbContext del scope: la siembra de tasas corre después del commit de Companies).
    [Fact]
    public async Task NewTenantCreatesTheCompanyAndSeedsTheColombianVat()
    {
        var (wholesaler, _, token) = await WholesaleApiHarness.InvitedAsync(Factory, NewEmail());
        var userId = await WholesaleApiHarness.PasswordSessionAsync(Factory, token, Password);
        using var manager = WholesaleApiHarness.CreateManager(Factory, wholesaler);
        var medellin = await WholesaleApiHarness.MedellinAsync(manager);

        var response = await AcceptNewTenantAsync(userId, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<AcceptedInvitationDto>(Ct))!;
        var company = Assert.Single(await WholesaleApiHarness.RowsAsync<CompanyRow>(
            Factory,
            $"""
            SELECT name AS "Name", tax_id AS "TaxId", city_id AS "CityId", address AS "Address"
            FROM companies.companies WHERE tenant_id = {accepted.TenantId}
            """));
        Assert.Equal(
            (WholesaleApiHarness.DefaultBusinessName, "900123456", medellin, "Calle 10 # 40-20"),
            (company.Name, company.TaxId, company.CityId, company.Address));
        var vat = await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""
            SELECT count(*)::int AS "Value" FROM catalog.tax_rates
            WHERE tenant_id = {accepted.TenantId} AND percentage = 19
            """);
        Assert.True(vat[0] >= 1, "The Colombian VAT was not seeded for the new tenant.");
    }

    // Spec 2026-11-23 §3 (idempotencia): repetir el accept es la misma respuesta de siempre y no duplica la empresa.
    [Fact]
    public async Task ARetriedAcceptDoesNotDuplicateTheCompany()
    {
        var (_, _, token) = await WholesaleApiHarness.InvitedAsync(Factory, NewEmail());
        var userId = await WholesaleApiHarness.PasswordSessionAsync(Factory, token, Password);
        var first = (await (await AcceptNewTenantAsync(userId, token)).Content.ReadFromJsonAsync<AcceptedInvitationDto>(Ct))!;

        var again = await AcceptNewTenantAsync(userId, token);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var companies = await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""SELECT count(*)::int AS "Value" FROM companies.companies WHERE tenant_id = {first.TenantId}""");
        Assert.Equal(1, companies[0]);
    }

    // Review Focus 3: la API ya no deja invitar sin ciudad, así que la fila de antes de la migración se simula quitándola.
    [Fact]
    public async Task AnOldInvitationWithoutCityAcceptsWithoutACompany()
    {
        var (_, distributor, token) = await WholesaleApiHarness.InvitedAsync(Factory, NewEmail());
        await WholesaleApiHarness.ExecuteAsync(
            Factory,
            $"UPDATE wholesale.distributors SET city_id = NULL, address = NULL WHERE id = {distributor.Id}");
        var userId = await WholesaleApiHarness.PasswordSessionAsync(Factory, token, Password);

        var response = await AcceptNewTenantAsync(userId, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<AcceptedInvitationDto>(Ct))!;
        Assert.True(accepted.Created);
        var companies = await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""SELECT count(*)::int AS "Value" FROM companies.companies WHERE tenant_id = {accepted.TenantId}""");
        Assert.Equal(0, companies[0]);
    }

    // Spec 2026-11-23 §4: el cliente del mayorista nace con el celular del aviso. Invitar con celular exige una conexión de
    // WhatsApp (otro host), así que el celular que ya normalizó invitar se deja directo en la fila.
    [Fact]
    public async Task TheWholesalerCustomerIsBornWithThePhone()
    {
        var (wholesaler, distributor, token) = await WholesaleApiHarness.InvitedAsync(Factory, NewEmail(), "901333444");
        await WholesaleApiHarness.ExecuteAsync(
            Factory, $"UPDATE wholesale.distributors SET phone = '+573001234567' WHERE id = {distributor.Id}");
        var userId = await WholesaleApiHarness.PasswordSessionAsync(Factory, token, Password);

        (await AcceptNewTenantAsync(userId, token)).EnsureSuccessStatusCode();

        var phones = await WholesaleApiHarness.RowsAsync<string>(
            Factory,
            $"""
            SELECT phone_e164 AS "Value" FROM customers.customers
            WHERE tenant_id = {wholesaler.TenantId} AND identification_number = '901333444'
            """);
        Assert.Equal(["+573001234567"], phones);
    }

    // Decisión del owner 2026-11-24: con teléfono, el cliente del mayorista nace completo con la ciudad y la dirección del
    // distribuidor, y su contacto es la etiqueta de la dirección principal. Sin teléfono (la invitación no lo trae) queda incompleto.
    [Fact]
    public async Task TheWholesalerCustomerIsBornCompleteWithTheCityAddressAndContact()
    {
        var (wholesaler, distributor, token) = await WholesaleApiHarness.InvitedAsync(Factory, NewEmail(), "901333555");
        await WholesaleApiHarness.ExecuteAsync(
            Factory, $"UPDATE wholesale.distributors SET phone = '+573001234567' WHERE id = {distributor.Id}");
        var userId = await WholesaleApiHarness.PasswordSessionAsync(Factory, token, Password);

        (await AcceptNewTenantAsync(userId, token)).EnsureSuccessStatusCode();

        var labels = await WholesaleApiHarness.RowsAsync<string>(
            Factory,
            $"""
            SELECT a.name AS "Value" FROM customers.customer_addresses a
            JOIN customers.customers c ON c.id = a.customer_id
            WHERE c.tenant_id = {wholesaler.TenantId} AND c.identification_number = '901333555' AND c.completeness = 'Complete'
              AND a.address = 'Calle 10 # 40-20' AND a.is_principal
            """);
        Assert.Equal(["Ana Pérez"], labels);
    }

    [Fact]
    public async Task TheWholesalerCustomerWithoutPhoneStaysIncomplete()
    {
        var (wholesaler, _, token) = await WholesaleApiHarness.InvitedAsync(Factory, NewEmail(), "901333666");
        var userId = await WholesaleApiHarness.PasswordSessionAsync(Factory, token, Password);

        (await AcceptNewTenantAsync(userId, token)).EnsureSuccessStatusCode();

        var states = await WholesaleApiHarness.RowsAsync<string>(
            Factory,
            $"""
            SELECT completeness AS "Value" FROM customers.customers
            WHERE tenant_id = {wholesaler.TenantId} AND identification_number = '901333666'
            """);
        Assert.Equal(["Incomplete"], states);
    }

    private sealed record CompanyRow(string Name, string TaxId, Guid CityId, string Address);

    // Spec 2026-11-19 §3b, foco 3: dos distribuidores con la misma razón social reciben la misma sugerencia; el que pierde la
    // reserva del slug reintenta con una sugerencia nueva. Host propio: el doble de las sugerencias es global.
    [Fact]
    public async Task TwoAcceptancesRacingForTheSameSlugBothEndUpWithATenant()
    {
        var race = new SlugRace();
        await using var database = await WholesaleApiHarness.StartDatabaseAsync();
        await using var factory = new WholesaleApiHarness.QepApiFactory(
            database.GetConnectionString(), configureServices: services => race.Install(services));
        var businessName = "Carrera Distribuidora SAS";
        var (_, _, firstToken) = await WholesaleApiHarness.InvitedAsync(factory, NewEmail(), "901000001", businessName);
        var (_, _, secondToken) = await WholesaleApiHarness.InvitedAsync(factory, NewEmail(), "901000002", businessName);
        var firstUser = await WholesaleApiHarness.PasswordSessionAsync(factory, firstToken, Password);
        var secondUser = await WholesaleApiHarness.PasswordSessionAsync(factory, secondToken, Password);

        var responses = await Task.WhenAll(
            AcceptNewTenantAsync(factory, firstUser, firstToken), AcceptNewTenantAsync(factory, secondUser, secondToken));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var accepted = await Task.WhenAll(responses.Select(response => response.Content.ReadFromJsonAsync<AcceptedInvitationDto>(Ct)));
        Assert.NotEqual(accepted[0]!.TenantId, accepted[1]!.TenantId);
        var slugs = await WholesaleApiHarness.RowsAsync<string>(
            factory,
            $"""SELECT slug AS "Value" FROM tenancy.tenants WHERE display_name = {businessName} ORDER BY slug""");
        Assert.Equal(["carrera-distribuidora", "carrera-distribuidora-2"], slugs);
        Assert.True(race.Calls >= 3, $"Only {race.Calls} suggestions: nobody retried.");
    }

    // Una aceptación a medias vinculando un tenant existente no deja tenant reservado: la marca, confirmada antes de prender
    // módulos, es lo que le cierra el documento al mayorista. Host propio: cambia el aprovisionamiento de clientes.
    [Fact]
    public async Task AHalfDoneLinkLocksTheDocumentAndStalesTheVersionFromBeforeTheStart()
    {
        await using var database = await WholesaleApiHarness.StartDatabaseAsync();
        await using var factory = new WholesaleApiHarness.QepApiFactory(
            database.GetConnectionString(),
            configureServices: services =>
            {
                services.RemoveAll<IWholesaleCustomerProvisioning>();
                services.AddScoped<IWholesaleCustomerProvisioning, CustomersDown>();
            });
        var mine = await WholesaleApiHarness.NewTenantAsync(factory);
        var (wholesaler, invited, token) = await WholesaleApiHarness.InvitedAsync(factory, WholesaleApiHarness.EmailOf(mine));
        using var session = WholesaleApiHarness.CreateSession(factory, mine.SubjectId, mine.TenantId);

        var halfDone = await session.PostAsJsonAsync(
            $"{WholesaleApiHarness.InvitationUrl(token)}/accept", new { mode = "LinkTenant", tenantId = mine.TenantId }, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, halfDone.StatusCode);
        var reserved = await WholesaleApiHarness.RowsAsync<int>(
            factory,
            $"""
            SELECT count(*)::int AS "Value" FROM tenancy.tenants t
            JOIN wholesale.distributor_invitations i ON i.reserved_tenant_id = t.id WHERE i.distributor_id = {invited.Id}
            """);
        Assert.Equal(0, reserved[0]);
        using var manager = WholesaleApiHarness.CreateManager(factory, wholesaler);
        var stale = await ChangeDocumentAsync(manager, wholesaler, invited, invited.Version);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("concurrency.conflict", await WholesaleApiHarness.CodeOfAsync(stale));
        var url = $"{WholesaleApiHarness.DistributorsUrl(wholesaler.TenantId)}/{invited.Id}";
        var marked = (await manager.GetFromJsonAsync<DistributorDetailDto>(url, Ct))!;
        Assert.Equal(("Invited", invited.Version + 1), (marked.Status, marked.Version));

        var refused = await ChangeDocumentAsync(manager, wholesaler, marked, marked.Version);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("wholesale.distributor.acceptance_in_progress", await WholesaleApiHarness.CodeOfAsync(refused));
        var stored = (await manager.GetFromJsonAsync<DistributorDetailDto>(url, Ct))!;
        Assert.Equal((invited.IdentificationNumber, marked.Version), (stored.IdentificationNumber, stored.Version));
    }

    // La marca va después de todo lo que rechaza sin escribir: un accept rechazado no le bloquea el documento a nadie.
    [Fact]
    public async Task AnAcceptRejectedByValidationLeavesTheDocumentEditable()
    {
        var mine = await WholesaleApiHarness.NewTenantAsync(Factory);
        var (wholesaler, invited, token) = await WholesaleApiHarness.InvitedAsync(Factory, NewEmail());
        var userId = await WholesaleApiHarness.PasswordSessionAsync(Factory, token, Password);
        var takenSlug = (await WholesaleApiHarness.RowsAsync<string>(
            Factory, $"""SELECT slug AS "Value" FROM tenancy.tenants WHERE id = {mine.TenantId}"""))[0];
        using var session = WholesaleApiHarness.CreateSession(Factory, userId);
        var url = $"{WholesaleApiHarness.InvitationUrl(token)}/accept";

        var notOwner = await session.PostAsJsonAsync(url, new { mode = "LinkTenant", tenantId = mine.TenantId }, Ct);
        var slugTaken = await session.PostAsJsonAsync(url, new { mode = "NewTenant", slug = takenSlug }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, notOwner.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, slugTaken.StatusCode);
        var marked = await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""
            SELECT count(*)::int AS "Value" FROM wholesale.distributors
            WHERE id = {invited.Id} AND acceptance_started_at IS NOT NULL
            """);
        Assert.Equal(0, marked[0]);
        using var manager = WholesaleApiHarness.CreateManager(Factory, wholesaler);
        var changed = await ChangeDocumentAsync(manager, wholesaler, invited, invited.Version);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
    }

    // Spec 2026-11-19 §4: siempre 202, exista o no el correo o la invitación; sólo emite con la invitación pendiente.
    [Fact]
    public async Task EmailCodeAlwaysAnswers202()
    {
        var (_, _, token) = await WholesaleApiHarness.InvitedAsync(Factory, NewEmail());
        var (_, expiredDistributor, expiredToken) = await WholesaleApiHarness.InvitedAsync(Factory, NewEmail());
        await WholesaleApiHarness.ExecuteAsync(
            Factory,
            $"""
            UPDATE wholesale.distributor_invitations SET expires_at = now() - interval '1 minute'
            WHERE distributor_id = {expiredDistributor.Id}
            """);
        using var anonymous = WholesaleApiHarness.CreateAnonymous(Factory);
        var anyEmail = NewEmail();
        var expiredEmail = NewEmail();

        var pending = await anonymous.PostAsJsonAsync(
            $"{WholesaleApiHarness.InvitationUrl(token)}/email-code", new { email = anyEmail }, Ct);
        var expired = await anonymous.PostAsJsonAsync(
            $"{WholesaleApiHarness.InvitationUrl(expiredToken)}/email-code", new { email = expiredEmail }, Ct);
        var unknown = await anonymous.PostAsJsonAsync(
            $"{WholesaleApiHarness.InvitationUrl("no-existe")}/email-code", new { email = NewEmail() }, Ct);
        var malformed = await anonymous.PostAsJsonAsync(
            $"{WholesaleApiHarness.InvitationUrl(token)}/email-code", new { email = "no-es-un-correo" }, Ct);
        var noBody = await anonymous.PostAsync($"{WholesaleApiHarness.InvitationUrl(token)}/email-code", null, Ct);

        Assert.All(
            new[] { pending, expired, unknown, malformed, noBody },
            response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        Assert.Matches("^[0-9]{6}$", await WholesaleApiHarness.LatestEmailCodeAsync(Factory, anyEmail));
        Assert.Null(await WholesaleApiHarness.LatestEmailCodeAsync(Factory, expiredEmail));
    }

    // El token no queda en la auditoría ni en el log de fallas (platform.request_failures), aunque una aceptación falle.
    [Fact]
    public async Task TheTokenNeverAppearsInAuditOrRequestLogs()
    {
        var email = NewEmail();
        var (_, _, token) = await WholesaleApiHarness.InvitedAsync(Factory, email);
        var stranger = await WholesaleApiHarness.NewTenantAsync(Factory);
        using (var session = WholesaleApiHarness.CreateSession(Factory, stranger.SubjectId, stranger.TenantId))
        {
            // Sin términos aceptados: 422, una falla que el middleware guarda.
            var refused = await session.PostAsJsonAsync(
                $"{WholesaleApiHarness.InvitationUrl(token)}/accept", new { mode = "NewTenant" }, Ct);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        }

        using (var anonymous = WholesaleApiHarness.CreateAnonymous(Factory))
        {
            var weak = await anonymous.PostAsJsonAsync(
                $"{WholesaleApiHarness.InvitationUrl(token)}/session-with-password", new { email, password = "1" }, Ct);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, weak.StatusCode);
        }

        var userId = await WholesaleApiHarness.PasswordSessionAsync(Factory, token, Password);
        (await AcceptNewTenantAsync(userId, token)).EnsureSuccessStatusCode();

        var masked = await WholesaleApiHarness.EventuallyAsync(
            () => WholesaleApiHarness.RowsAsync<string>(
                Factory,
                $"""
                SELECT path AS "Value" FROM platform.request_failures
                WHERE path LIKE '/api/v1/wholesale-invitations/%' ORDER BY occurred_at
                """),
            rows => rows.Count >= 2);
        Assert.Contains("/api/v1/wholesale-invitations/***/accept", masked);
        Assert.Contains("/api/v1/wholesale-invitations/***/session-with-password", masked);
        var leaks = await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""
            SELECT ((SELECT count(*) FROM platform.request_failures f WHERE strpos(f::text, {token}) > 0)
                  + (SELECT count(*) FROM audit.entries e WHERE strpos(e::text, {token}) > 0))::int AS "Value"
            """);
        Assert.Equal(0, leaks[0]);
    }

    // Spec 2026-11-24, anti-objetivo: la prueba viaja sólo en el evento del correo. No sale en las respuestas de la API, no queda
    // en la auditoría ni en el log de fallas, y ningún otro evento del outbox la lleva (el aviso de WhatsApp se mira en
    // DistributorWhatsAppNoticeApiTests, que tiene la conexión lista).
    [Fact]
    public async Task TheEmailProofNeverAppearsInAuditOrRequestLogs()
    {
        var email = NewEmail();
        var (wholesaler, distributor, token) = await WholesaleApiHarness.InvitedAsync(Factory, email);
        var emailProof = await WholesaleApiHarness.LatestEmailProofAsync(Factory, distributor.Id);
        Assert.NotNull(emailProof);
        using var anonymous = WholesaleApiHarness.CreateAnonymous(Factory);
        using var manager = WholesaleApiHarness.CreateManager(Factory, wholesaler);
        var url = $"{WholesaleApiHarness.InvitationUrl(token)}/session-with-password";

        var summary = await anonymous.GetStringAsync(WholesaleApiHarness.InvitationUrl(token), Ct);
        var detail = await manager.GetStringAsync(
            $"{WholesaleApiHarness.DistributorsUrl(wholesaler.TenantId)}/{distributor.Id}", Ct);
        // Dos fallas con la prueba válida en el cuerpo, de las que el middleware guarda: sin términos (422) y otro correo (401).
        var withoutTerms = await anonymous.PostAsJsonAsync(url, new { email, password = Password, emailProof }, Ct);
        var anotherEmail = await anonymous.PostAsJsonAsync(
            url, new { email = NewEmail(), password = Password, emailProof }, Ct);
        var opened = await anonymous.PostAsJsonAsync(
            url, new { email, password = Password, termsVersion = WholesaleApiHarness.TermsVersion, emailProof }, Ct);
        var userId = (await opened.Content.ReadFromJsonAsync<SessionResponse>(Ct))!.UserId;
        var accepted = await AcceptNewTenantAsync(userId, token);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, withoutTerms.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anotherEmail.StatusCode);
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.NotNull(distributor.InvitationUrl);
        Assert.DoesNotContain("ev=", distributor.InvitationUrl, StringComparison.Ordinal);
        Assert.All(
            new[] { distributor.InvitationUrl, summary, detail, await WholesaleApiHarness.BodyAsync(accepted) },
            text => Assert.DoesNotContain(emailProof, text, StringComparison.Ordinal));
        // Las dos filas de estos dos requests, por su traceId: la ruta enmascarada es la misma para toda la clase.
        var withoutTermsTrace = await TraceIdOfAsync(withoutTerms);
        var anotherEmailTrace = await TraceIdOfAsync(anotherEmail);
        Assert.Equal(
            2,
            await WholesaleApiHarness.EventuallyAsync(
                () => FailureRowsAsync(withoutTermsTrace, anotherEmailTrace), count => count >= 2));
        var leaks = await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""
            SELECT ((SELECT count(*) FROM platform.request_failures f WHERE strpos(f::text, {emailProof}) > 0)
                  + (SELECT count(*) FROM audit.entries e WHERE strpos(e::text, {emailProof}) > 0)
                  + (SELECT count(*) FROM platform.outbox_messages o
                     WHERE o.event_name <> {WholesaleApiHarness.DistributorInvitedEvent}
                       AND strpos(o.payload::text, {emailProof}) > 0))::int AS "Value"
            """);
        Assert.Equal(0, leaks[0]);
    }

    private async Task<int> FailureRowsAsync(string firstTraceId, string secondTraceId) =>
        (await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""
            SELECT count(*)::int AS "Value" FROM platform.request_failures
            WHERE trace_id IN ({firstTraceId}, {secondTraceId})
            """))[0];

    /// <summary>El <c>traceId</c> del ProblemDetails, el mismo con el que se guarda la fila de la falla.</summary>
    private static async Task<string> TraceIdOfAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await WholesaleApiHarness.BodyAsync(response));
        return document.RootElement.GetProperty("traceId").GetString()!;
    }

    private static string NewEmail() => $"distri-{Guid.NewGuid():N}@distri.co";

    private static Task<HttpResponseMessage> ChangeDocumentAsync(
        HttpClient manager, WholesaleTestTenant wholesaler, DistributorDetailDto distributor, long version) =>
        manager.SendAsync(
            WholesaleApiHarness.WithIfMatch(
                new HttpRequestMessage(
                    HttpMethod.Put, $"{WholesaleApiHarness.DistributorsUrl(wholesaler.TenantId)}/{distributor.Id}")
                {
                    Content = JsonContent.Create(new
                    {
                        priceListId = distributor.PriceList.Id,
                        advisorId = distributor.Advisor.MemberId,
                        billingCompanyId = distributor.BillingCompany.Id,
                        identificationNumber = "800111222",
                    }),
                },
                version),
            Ct);

    // Customers caído: la aceptación se corta después de haber escrito en otros módulos.
    private sealed class CustomersDown : IWholesaleCustomerProvisioning
    {
        public Task<Guid> EnsureCustomerAsync(
            Guid wholesalerTenantId,
            Guid actorUserId,
            string businessName,
            string country,
            string identificationType,
            string identificationNumber,
            string email,
            string? phone,
            Guid? cityId,
            string? cityName,
            string? address,
            string contactName,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Customers is down.");
    }

    // Slug de 30: dentro del tope de Tenancy y único por prueba.
    private static string NewSlug() => $"distri-{Guid.NewGuid():N}"[..30];

    private Task<HttpResponseMessage> AcceptNewTenantAsync(Guid userId, string token) =>
        AcceptNewTenantAsync(Factory, userId, token);

    private static async Task<HttpResponseMessage> AcceptNewTenantAsync(
        WholesaleApiHarness.QepApiFactory factory, Guid userId, string token)
    {
        using var session = WholesaleApiHarness.CreateSession(factory, userId);
        return await session.PostAsJsonAsync($"{WholesaleApiHarness.InvitationUrl(token)}/accept", new { mode = "NewTenant" }, Ct);
    }

    /// <summary>La auditoría del cambio de correo y su evento para ese distribuidor.</summary>
    private async Task<int> EmailChangesAsync(Guid wholesalerTenantId, Guid distributorId)
    {
        var rows = await WholesaleApiHarness.RowsAsync<int>(
            Factory,
            $"""
            SELECT ((SELECT count(*) FROM audit.entries
                     WHERE tenant_id = {wholesalerTenantId} AND resource_id = {distributorId.ToString()}
                       AND action = 'wholesale.distributor.email_changed')
                  + (SELECT count(*) FROM platform.outbox_messages
                     WHERE event_name = {EmailChangedEvent} AND payload ->> 'distributorId' = {distributorId.ToString()}))::int AS "Value"
            """);
        return rows[0];
    }

    private async Task<int> CountUsersAsync(string email)
    {
        var rows = await WholesaleApiHarness.RowsAsync<int>(
            Factory, $"""SELECT count(*)::int AS "Value" FROM identity.users WHERE email = {email}""");
        return rows[0];
    }

    /// <summary>
    /// Las dos primeras sugerencias de slug devuelven la misma (la que vio la primera), como dos aceptaciones que consultan
    /// antes de que cualquiera reserve; desde la tercera, la sugerencia real.
    /// </summary>
    private sealed class SlugRace
    {
        private int _calls;
        private string? _first;

        public int Calls => Volatile.Read(ref _calls);

        public void Install(IServiceCollection services)
        {
            var original = services.Last(descriptor => descriptor.ServiceType == typeof(ITenantSlugSuggestions));
            services.Remove(original);
            services.AddScoped<ITenantSlugSuggestions>(provider => new Stale(
                (ITenantSlugSuggestions)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!), this));
        }

        private sealed class Stale(ITenantSlugSuggestions inner, SlugRace race) : ITenantSlugSuggestions
        {
            public async Task<string> SuggestAsync(string displayName, CancellationToken cancellationToken)
            {
                if (Interlocked.Increment(ref race._calls) <= 2)
                {
                    var suggested = await inner.SuggestAsync(displayName, cancellationToken);
                    return Interlocked.CompareExchange(ref race._first, suggested, null) ?? suggested;
                }

                return await inner.SuggestAsync(displayName, cancellationToken);
            }
        }
    }
}
