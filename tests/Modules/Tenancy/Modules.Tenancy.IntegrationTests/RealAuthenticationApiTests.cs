using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

// Ejercita la rama de autenticación real (sin el stub de desarrollo) — todas las demás
// pruebas de integración de este proyecto corren con Authentication:UseDevelopmentStub
// en true por defecto (entorno Development), así que ninguna toca de verdad
// SessionCookieAuthenticationHandler, el pinning del esquema GoogleBearer,
// RequireCsrfHeaderMiddleware ni SessionRevocationWorker. Esta suite se auto-emite un JWT
// con forma de Google (Authentication:TestSigningKey — ver QepServiceCollectionExtensions.AddAuthentication)
// así que nunca depende del endpoint de discovery OIDC vivo de Google.
public sealed class RealAuthenticationApiTests
{
    private const string Issuer = "https://accounts.google.com";
    private const string Audience = "test-audience";
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);
    private static readonly byte[] SigningKeyBytes = RandomNumberGenerator.GetBytes(32);
    private static readonly string SigningKeyBase64 = Convert.ToBase64String(SigningKeyBytes);
    private static readonly string[] AdvisorRoles = ["advisor"];
    private static readonly string[] AdminRoles = ["admin"];

    [Fact]
    public async Task SessionCookieAuthenticatesOrdinaryEndpointsWithoutTheBearerToken()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());

        var (owner, tenantId) = await RegisterOwnerAndTenantAsync(factory);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/tenants/{tenantId}/settings");
        request.Headers.Add("X-Tenant-Id", tenantId.ToString());
        var response = await owner.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GoogleBearerTokenCannotAuthenticateAnOrdinaryEndpoint()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());

        var (owner, tenantId) = await RegisterOwnerAndTenantAsync(factory);
        var url = $"/api/v1/tenants/{tenantId}/settings";

        // El control positivo, y no es decorado: mientras SDD-CT-14 estuvo abierta TODO el flujo
        // respondía Unauthorized, así que esta prueba pasaba esperando exactamente lo que estaba
        // roto — verde sin verificar nada. Afirmar primero que el MISMO endpoint sí responde con
        // la cookie es lo que hace que el 401 de abajo signifique «lo rechazó el pinning de
        // esquema» y no «acá no entra nadie».
        using var withCookie = new HttpRequestMessage(HttpMethod.Get, url);
        withCookie.Headers.Add("X-Tenant-Id", tenantId.ToString());
        var allowed = await owner.SendAsync(withCookie, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        // Un cliente nuevo sin cookie, sólo con un bearer token de Google todavía válido —
        // este es exactamente el bypass que la separación de esquemas GoogleBearer/QepSession
        // (QepServiceCollectionExtensions.AddAuthentication) existe para impedir.
        using var bearerOnlyClient = CreateClient(factory);
        var token = IssueGoogleIdToken(Guid.NewGuid().ToString(), NewEmail(), emailVerified: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Tenant-Id", tenantId.ToString());

        var response = await bearerOnlyClient.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MutatingRequestWithoutCsrfHeaderIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());

        var (owner, tenantId) = await RegisterOwnerAndTenantAsync(factory);
        var etag = await GetSettingsEtagAsync(owner, tenantId);

        using var withoutHeader = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/v1/tenants/{tenantId}/settings")
        {
            Content = JsonContent.Create(NewSettingsBody()),
        };
        withoutHeader.Headers.Add("X-Tenant-Id", tenantId.ToString());
        withoutHeader.Headers.TryAddWithoutValidation("If-Match", etag);
        var rejected = await owner.SendAsync(
            withoutHeader,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);

        using var withHeader = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/v1/tenants/{tenantId}/settings")
        {
            Content = JsonContent.Create(NewSettingsBody()),
        };
        withHeader.Headers.Add("X-Tenant-Id", tenantId.ToString());
        withHeader.Headers.Add("X-Qep-Client", "web");
        withHeader.Headers.TryAddWithoutValidation("If-Match", etag);
        var accepted = await owner.SendAsync(withHeader, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task LogoutRevokesTheSessionCookie()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());

        var (owner, _) = await RegisterOwnerAndTenantAsync(factory);

        using var logoutRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/auth/logout");
        logoutRequest.Headers.Add("X-Qep-Client", "web");
        var logoutResponse = await owner.SendAsync(
            logoutRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

        var meResponse = await owner.GetAsync(
            "/api/v1/auth/me",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, meResponse.StatusCode);
    }

    [Fact]
    public async Task SuspendingMembershipRevokesTheMembersActiveSession()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());

        var (owner, tenantId) = await RegisterOwnerAndTenantAsync(factory);

        // El owner invita a un segundo usuario real, que después entra de verdad (también
        // por el /auth/session fijado a GoogleBearer, estableciendo su propia
        // cookie de sesión) — el atajo "confiar en cualquier header" del stub de desarrollo
        // no existe en esta rama, así que esto es un invitar+aceptar+entrar completo y real.
        var memberEmail = NewEmail();
        using var inviteRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{tenantId}/memberships")
        {
            Content = JsonContent.Create(new { email = memberEmail, roles = AdvisorRoles }),
        };
        inviteRequest.Headers.Add("X-Tenant-Id", tenantId.ToString());
        inviteRequest.Headers.Add("X-Qep-Client", "web");
        var inviteResponse = await owner.SendAsync(
            inviteRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, inviteResponse.StatusCode);
        var invited = await inviteResponse.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(invited);

        using var member = CreateClient(factory);
        var memberToken = IssueGoogleIdToken(
            Guid.NewGuid().ToString(),
            memberEmail,
            emailVerified: true);
        using var loginRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/auth/session");
        loginRequest.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", memberToken);
        loginRequest.Headers.Add("X-Qep-Client", "web");
        var loginResponse = await member.SendAsync(
            loginRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        // Confirmar que la sesión del miembro está viva antes de suspenderlo.
        var beforeSuspend = await member.GetAsync(
            "/api/v1/auth/me",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, beforeSuspend.StatusCode);

        using var suspendRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{tenantId}/memberships/{invited!.Id}/suspend");
        suspendRequest.Headers.Add("X-Tenant-Id", tenantId.ToString());
        suspendRequest.Headers.Add("X-Qep-Client", "web");
        var suspendResponse = await owner.SendAsync(
            suspendRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, suspendResponse.StatusCode);

        // SessionRevocationWorker consume tenancy.membership-suspended.v1 del
        // outbox por temporizador (ver SessionRevocationWorker) — sondear hasta que la
        // cookie de sesión del miembro deje de autenticar.
        await WaitUntilAsync(async () =>
        {
            var response = await member.GetAsync(
                "/api/v1/auth/me",
                TestContext.Current.CancellationToken);
            return response.StatusCode == HttpStatusCode.Unauthorized;
        });
    }

    [Fact]
    public async Task RoleDowngradeRemovesPermissionsOnTheNextRequest()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());

        var (owner, tenantId) = await RegisterOwnerAndTenantAsync(factory);

        var secondOwnerEmail = NewEmail();
        using var inviteRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{tenantId}/memberships")
        {
            Content = JsonContent.Create(new { email = secondOwnerEmail, roles = AdvisorRoles }),
        };
        inviteRequest.Headers.Add("X-Tenant-Id", tenantId.ToString());
        inviteRequest.Headers.Add("X-Qep-Client", "web");
        var inviteResponse = await owner.SendAsync(
            inviteRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, inviteResponse.StatusCode);
        var invited = await inviteResponse.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(invited);

        // El admin no se asigna por invitación (InviteMember.EnsureInvitableRoles) — se
        // invita con un rol invitable y se promueve después, por traspaso explícito.
        using var promoteRequest = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/v1/tenants/{tenantId}/memberships/{invited!.Id}/roles")
        {
            Content = JsonContent.Create(new { roles = AdminRoles }),
        };
        promoteRequest.Headers.Add("X-Tenant-Id", tenantId.ToString());
        promoteRequest.Headers.Add("X-Qep-Client", "web");
        promoteRequest.Headers.TryAddWithoutValidation("If-Match", $"\"{invited.Version}\"");
        var promoteResponse = await owner.SendAsync(
            promoteRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, promoteResponse.StatusCode);

        using var secondOwner = CreateClient(factory);
        var token = IssueGoogleIdToken(
            Guid.NewGuid().ToString(),
            secondOwnerEmail,
            emailVerified: true);
        using var loginRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/auth/session");
        loginRequest.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        loginRequest.Headers.Add("X-Qep-Client", "web");
        var loginResponse = await secondOwner.SendAsync(
            loginRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        using var beforeDowngrade = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{tenantId}/memberships")
        {
            Content = JsonContent.Create(new { email = NewEmail(), roles = AdvisorRoles }),
        };
        beforeDowngrade.Headers.Add("X-Tenant-Id", tenantId.ToString());
        beforeDowngrade.Headers.Add("X-Qep-Client", "web");
        var allowed = await secondOwner.SendAsync(
            beforeDowngrade,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);

        var activeSecondOwner = await FindMembershipAsync(owner, tenantId, invited!.Id);
        using var downgradeRequest = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/v1/tenants/{tenantId}/memberships/{invited.Id}/roles")
        {
            Content = JsonContent.Create(new { roles = AdvisorRoles }),
        };
        downgradeRequest.Headers.Add("X-Tenant-Id", tenantId.ToString());
        downgradeRequest.Headers.Add("X-Qep-Client", "web");
        downgradeRequest.Headers.TryAddWithoutValidation(
            "If-Match",
            $"\"{activeSecondOwner.Version}\"");
        var downgradeResponse = await owner.SendAsync(
            downgradeRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, downgradeResponse.StatusCode);

        using var afterDowngrade = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{tenantId}/memberships")
        {
            Content = JsonContent.Create(new { email = NewEmail(), roles = AdvisorRoles }),
        };
        afterDowngrade.Headers.Add("X-Tenant-Id", tenantId.ToString());
        afterDowngrade.Headers.Add("X-Qep-Client", "web");
        var denied = await secondOwner.SendAsync(
            afterDowngrade,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    // Todo cliente de esta suite se crea acá, y sobre https. Ver CreateClient.
    private static async Task<(HttpClient Client, Guid TenantId)> RegisterOwnerAndTenantAsync(
        QepApiFactory factory)
    {
        var client = CreateClient(factory);
        var token = IssueGoogleIdToken(Guid.NewGuid().ToString(), NewEmail(), emailVerified: true);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/auth/register-tenant")
        {
            Content = JsonContent.Create(new
            {
                displayName = "Real Auth Test Co",
                slug = $"real-auth-{Guid.NewGuid():N}"[..24],
                defaultCulture = "es-CO",
                timeZone = "America/Bogota",
                dateFormat = "yyyy-MM-dd",
            }),
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Qep-Client", "web");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var registered = await response.Content.ReadFromJsonAsync<RegisterTenantPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(registered);
        return (client, registered!.TenantId);
    }

    private static async Task<string> GetSettingsEtagAsync(HttpClient client, Guid tenantId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/tenants/{tenantId}/settings");
        request.Headers.Add("X-Tenant-Id", tenantId.ToString());
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
        return response.Headers.ETag!.Tag;
    }

    private static async Task<MembershipPayload> FindMembershipAsync(
        HttpClient client,
        Guid tenantId,
        Guid membershipId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/tenants/{tenantId}/memberships");
        request.Headers.Add("X-Tenant-Id", tenantId.ToString());
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<MembershipListPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(list);
        return Assert.Single(list!.Items, membership => membership.Id == membershipId);
    }

    private static object NewSettingsBody() => new
    {
        displayName = $"QCode {Guid.NewGuid():N}"[..24],
        defaultCulture = "es-CO",
        timeZone = "America/Bogota",
        dateFormat = "dd/MM/yyyy",
    };

    private static string NewEmail() => $"real-auth-{Guid.NewGuid():N}@example.com";

    private static string IssueGoogleIdToken(string subject, string email, bool emailVerified)
    {
        var handler = new JwtSecurityTokenHandler();
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(SigningKeyBytes),
            SecurityAlgorithms.HmacSha256);
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims:
            [
                new Claim("sub", subject),
                new Claim("email", email),
                new Claim("email_verified", emailVerified ? "true" : "false"),
            ],
            notBefore: now.AddMinutes(-1),
            expires: now.AddMinutes(30),
            signingCredentials: credentials);
        return handler.WriteToken(token);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < PollTimeout)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Condition was not met within {PollTimeout.TotalSeconds:0} seconds.");
    }

    private static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }

    private sealed record RegisterTenantPayload(Guid TenantId, Guid OwnerUserId);

    private sealed record MembershipPayload(Guid Id, Guid UserId, long Version);

    /// <summary>El listado viaja envuelto, con los conteos por estado al lado.</summary>
    private sealed record MembershipListPayload(
        IReadOnlyList<MembershipPayload> Items);

    /// <summary>
    /// Cliente sobre **https**, y no es cosmético: es lo que hace que la cookie de sesión viaje.
    ///
    /// `SessionCookieWriter` marca la cookie `Secure` en todo entorno que no sea `Development` ni
    /// `Local` (`SessionCookieWriter.cs:20`), y esta suite corre a propósito en `IntegrationTests`
    /// para ejercitar la rama de auth real. Con el `http://localhost` que
    /// `WebApplicationFactory` usa por defecto, el `CookieContainer` **acepta** la cookie y
    /// **no la reenvía**: el servidor la emite, el cliente la guarda, y ningún request posterior
    /// la lleva. El síntoma es `Unauthorized` en cualquier punto del flujo, que no se parece en
    /// nada a su causa — y fue `SDD-CT-14` durante cuatro slices.
    ///
    /// Centralizado para que no se desincronice, por el mismo criterio con el que
    /// `SessionCookieWriter` centraliza los flags de la cookie del lado del servidor.
    /// </summary>
    private static HttpClient CreateClient(QepApiFactory factory) =>
        factory.CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    private sealed class QepApiFactory(string connectionString)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // No "Development": fuera de ese entorno UseDevelopmentStub queda apagado por
            // defecto, así que esto ejercita la rama real GoogleBearer/QepSession — ver
            // QepServiceCollectionExtensions.AddAuthentication.
            //
            // Tampoco "Local", que es lo que era antes: ese nombre hace que Program cargue
            // los user-secrets (src/Api/Program.cs:23-26) *después* de los valores de UseSetting
            // de abajo, así que el secreto ConnectionStrings:QepDatabase de un developer ganaba
            // en silencio y estas pruebas corrían contra la base de desarrollo real — fallando
            // cuando estaba caída y escribiéndole cuando estaba arriba. Cualquier nombre fuera de
            // "Development" y "Local" mantiene la rama de auth real sin ese override. SDD-CT-14.
            builder.UseEnvironment("IntegrationTests");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Fijado, no heredado: appsettings.json lleva el proveedor con el que se despliega el
            // producto, y una suite de integración que depende de eso termina dependiendo de las
            // credenciales de quien la corra. Con "infobip" y las claves de Infobip ausentes —CI,
            // un clon nuevo— NotificationsOptionsValidator falla al arrancar y todas las pruebas
            // del archivo mueren antes de llegar a su aserción.
            // El canal de log es el default de desarrollo (SDD-CT-03). SDD-CT-17.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Registration:PublicTenantSignupEnabled", "true");
            builder.UseSetting("Authentication:Audience", Audience);
            builder.UseSetting("Authentication:TestSigningKey", SigningKeyBase64);
        }
    }
}
