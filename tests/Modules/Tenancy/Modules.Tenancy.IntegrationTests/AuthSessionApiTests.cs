using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

public sealed class AuthSessionApiTests
{
    private const string SeededTenantId = "01900000-0000-7000-8000-000000000001";
    private const string AdminSubjectId = "01900000-0000-7000-8000-000000000002";
    private static readonly string[] DefaultRoles = ["tenancy.member"];

    [Fact]
    public async Task FirstLoginLinksActivatesAndAcceptsInvitedMembership()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var email = NewEmail();
        var googleSubject = Guid.CreateVersion7().ToString();

        // An admin invites the email into the seeded tenant.
        using (var admin = CreateAdminClient(factory))
        {
            var invite = await InviteAsync(admin, email);
            Assert.Equal(HttpStatusCode.Created, invite.StatusCode);
        }

        // The invited user logs in with Google (simulated verified email).
        using var client = CreateLoginClient(factory, googleSubject, email, verified: true);
        var response = await client.PostAsync(
            "/api/v1/auth/session",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await response.Content.ReadFromJsonAsync<SessionPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(session);
        Assert.Equal(email, session!.Email);
        Assert.Contains(Guid.Parse(SeededTenantId), session.ActiveTenantIds);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var user = await QueryRowAsync(
            connection,
            "SELECT status FROM identity.users WHERE email = @email",
            ("email", email));
        Assert.Equal("Active", user![0]);

        var link = await QueryRowAsync(
            connection,
            "SELECT subject FROM identity.provider_links WHERE provider = 'google' AND subject = @subject",
            ("subject", googleSubject));
        Assert.NotNull(link);

        var membership = await QueryRowAsync(
            connection,
            "SELECT state FROM tenancy.memberships WHERE user_id = @userId",
            ("userId", session.UserId));
        Assert.Equal("Active", membership![0]);
    }

    [Fact]
    public async Task LoginWithUninvitedEmailIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());

        using var client = CreateLoginClient(
            factory,
            Guid.CreateVersion7().ToString(),
            NewEmail(),
            verified: true);
        var response = await client.PostAsync(
            "/api/v1/auth/session",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task LoginWithUnverifiedEmailIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var email = NewEmail();

        using (var admin = CreateAdminClient(factory))
        {
            await InviteAsync(admin, email);
        }

        using var client = CreateLoginClient(
            factory,
            Guid.CreateVersion7().ToString(),
            email,
            verified: false);
        var response = await client.PostAsync(
            "/api/v1/auth/session",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RepeatedLoginIsIdempotent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var email = NewEmail();
        var googleSubject = Guid.CreateVersion7().ToString();

        using (var admin = CreateAdminClient(factory))
        {
            await InviteAsync(admin, email);
        }

        using var client = CreateLoginClient(factory, googleSubject, email, verified: true);

        var first = await client.PostAsync(
            "/api/v1/auth/session", null, TestContext.Current.CancellationToken);
        var second = await client.PostAsync(
            "/api/v1/auth/session", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstSession = await first.Content.ReadFromJsonAsync<SessionPayload>(
            TestContext.Current.CancellationToken);
        var secondSession = await second.Content.ReadFromJsonAsync<SessionPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal(firstSession!.UserId, secondSession!.UserId);
        Assert.Contains(Guid.Parse(SeededTenantId), secondSession.ActiveTenantIds);
    }

    private static string NewEmail() => $"login-{Guid.NewGuid():N}@example.com";

    private static async Task<HttpResponseMessage> InviteAsync(HttpClient client, string email)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{SeededTenantId}/memberships")
        {
            Content = JsonContent.Create(new { email, roles = DefaultRoles })
        };
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<string[]?> QueryRowAsync(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await using var reader = await command.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);
        if (!await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            return null;
        }

        var values = new string[reader.FieldCount];
        for (var index = 0; index < reader.FieldCount; index++)
        {
            values[index] = reader.GetValue(index).ToString() ?? string.Empty;
        }

        return values;
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

    private static HttpClient CreateAdminClient(QepApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", AdminSubjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", SeededTenantId);
        return client;
    }

    private static HttpClient CreateLoginClient(
        QepApiFactory factory,
        string googleSubject,
        string email,
        bool verified)
    {
        var client = factory.CreateClient();
        // X-Subject-Id carries the provider subject; X-Tenant-Id is a dummy required
        // by the development auth stub. Login itself is tenant-agnostic.
        client.DefaultRequestHeaders.Add("X-Subject-Id", googleSubject);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", SeededTenantId);
        client.DefaultRequestHeaders.Add("X-Email", email);
        client.DefaultRequestHeaders.Add("X-Email-Verified", verified ? "true" : "false");
        return client;
    }

    private sealed record SessionPayload(
        Guid UserId,
        string? Email,
        IReadOnlyCollection<Guid> ActiveTenantIds);

    private sealed class QepApiFactory(string connectionString)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
        }
    }
}
