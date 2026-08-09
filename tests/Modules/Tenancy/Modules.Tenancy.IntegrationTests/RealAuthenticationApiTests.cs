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

// Exercises the real (non-dev-stub) authentication branch — every other integration
// test in this project runs with Authentication:UseDevelopmentStub defaulted on
// (Development environment), so none of them ever touch SessionCookieAuthenticationHandler,
// the GoogleBearer scheme pinning, RequireCsrfHeaderMiddleware, or
// SessionRevocationWorker for real. This suite self-issues a Google-shaped JWT
// (Authentication:TestSigningKey — see QepServiceCollectionExtensions.AddAuthentication)
// so it never depends on Google's live OIDC discovery endpoint.
public sealed class RealAuthenticationApiTests
{
    private const string Issuer = "https://accounts.google.com";
    private const string Audience = "test-audience";
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);
    private static readonly byte[] SigningKeyBytes = RandomNumberGenerator.GetBytes(32);
    private static readonly string SigningKeyBase64 = Convert.ToBase64String(SigningKeyBytes);
    private static readonly string[] MemberRoles = ["tenancy.member"];
    private static readonly string[] OwnerRoles = ["tenancy.owner"];

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

        var (_, tenantId) = await RegisterOwnerAndTenantAsync(factory);

        // A fresh client with no cookie, only a still-valid Google bearer token —
        // this is exactly the bypass the GoogleBearer/QepSession scheme split
        // (QepServiceCollectionExtensions.AddAuthentication) exists to prevent.
        using var bearerOnlyClient = factory.CreateClient();
        var token = IssueGoogleIdToken(Guid.NewGuid().ToString(), NewEmail(), emailVerified: true);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/tenants/{tenantId}/settings");
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

        // Owner invites a second real user, who then logs in for real (also
        // through the GoogleBearer-pinned /auth/session, establishing their own
        // session cookie) — the dev-stub's "trust any header" shortcut doesn't
        // exist on this branch, so this is a full, real invite+accept+login.
        var memberEmail = NewEmail();
        using var inviteRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{tenantId}/memberships")
        {
            Content = JsonContent.Create(new { email = memberEmail, roles = MemberRoles }),
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

        using var member = factory.CreateClient();
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

        // Confirm the member's session is live before suspending them.
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

        // SessionRevocationWorker consumes tenancy.membership-suspended.v1 off the
        // outbox on a timer (see SessionRevocationWorker) — poll until the
        // member's session cookie stops authenticating.
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
            Content = JsonContent.Create(new { email = secondOwnerEmail, roles = OwnerRoles }),
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

        using var secondOwner = factory.CreateClient();
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
            Content = JsonContent.Create(new { email = NewEmail(), roles = MemberRoles }),
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
            Content = JsonContent.Create(new { roles = MemberRoles }),
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
            Content = JsonContent.Create(new { email = NewEmail(), roles = MemberRoles }),
        };
        afterDowngrade.Headers.Add("X-Tenant-Id", tenantId.ToString());
        afterDowngrade.Headers.Add("X-Qep-Client", "web");
        var denied = await secondOwner.SendAsync(
            afterDowngrade,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    private static async Task<(HttpClient Client, Guid TenantId)> RegisterOwnerAndTenantAsync(
        QepApiFactory factory)
    {
        var client = factory.CreateClient();
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
        var memberships = await response.Content.ReadFromJsonAsync<MembershipPayload[]>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(memberships);
        return Assert.Single(memberships!, membership => membership.Id == membershipId);
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

    private sealed class QepApiFactory(string connectionString)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Not "Development": UseDevelopmentStub defaults off outside it, so this
            // exercises the real GoogleBearer/QepSession branch — see
            // QepServiceCollectionExtensions.AddAuthentication.
            //
            // Not "Local" either, which is what this used to be: that name makes Program
            // load user-secrets (src/Api/Program.cs:23-26) *after* the UseSetting values
            // below, so a developer's ConnectionStrings:QepDatabase secret silently won
            // and these tests ran against the real development database — failing when it
            // was down and writing to it when it was up. Any name outside "Development"
            // and "Local" keeps the real auth branch without that override. SDD-CT-14.
            builder.UseEnvironment("IntegrationTests");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Pinned, not inherited: appsettings.json carries whatever provider the product
            // is deployed with, and an integration suite that depends on that ends up
            // depending on the credentials of whoever runs it. With "infobip" and the
            // Infobip keys absent — CI, a fresh clone — NotificationsOptionsValidator fails
            // at startup and every test in the file dies before reaching its assertion.
            // The log channel is the development default (SDD-CT-03). SDD-CT-17.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Registration:PublicTenantSignupEnabled", "true");
            builder.UseSetting("Authentication:Audience", Audience);
            builder.UseSetting("Authentication:TestSigningKey", SigningKeyBase64);
        }
    }
}
