using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

public sealed class RegistrationApiTests
{
    [Fact]
    public async Task PolicyEndpointReflectsTheFlag()
    {
        await using var database = await StartDatabaseAsync();

        using (var disabled = new QepApiFactory(database.GetConnectionString(), false))
        using (var client = disabled.CreateClient())
        {
            var policy = await client.GetFromJsonAsync<PolicyPayload>(
                "/api/v1/auth/registration-policy",
                TestContext.Current.CancellationToken);
            Assert.False(policy!.PublicTenantSignupEnabled);
        }

        using (var enabled = new QepApiFactory(database.GetConnectionString(), true))
        using (var client = enabled.CreateClient())
        {
            var policy = await client.GetFromJsonAsync<PolicyPayload>(
                "/api/v1/auth/registration-policy",
                TestContext.Current.CancellationToken);
            Assert.True(policy!.PublicTenantSignupEnabled);
        }
    }

    [Fact]
    public async Task RegisterTenantIsForbiddenWhenSignupDisabled()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), false);
        using var client = CreateOwnerClient(factory, NewEmail());

        var response = await RegisterAsync(client, NewSlug());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RegisterTenantCreatesActiveTenantAndOwnerMembership()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), true);
        var email = NewEmail();
        using var client = CreateOwnerClient(factory, email);

        var response = await RegisterAsync(client, NewSlug());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var registered = await response.Content.ReadFromJsonAsync<RegisterPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(registered);
        Assert.NotEqual(Guid.Empty, registered!.TenantId);
        Assert.NotEqual(Guid.Empty, registered.OwnerUserId);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var tenant = await QueryRowAsync(
            connection,
            "SELECT status FROM tenancy.tenants WHERE id = @id",
            ("id", registered.TenantId));
        Assert.Equal("Active", tenant![0]);

        var owner = await QueryRowAsync(
            connection,
            "SELECT status FROM identity.users WHERE email = @email",
            ("email", email));
        Assert.Equal("Active", owner![0]);

        var membership = await QueryRowAsync(
            connection,
            """
            SELECT state FROM tenancy.memberships
            WHERE tenant_id = @tenantId AND user_id = @userId
            """,
            ("tenantId", registered.TenantId),
            ("userId", registered.OwnerUserId));
        Assert.Equal("Active", membership![0]);
    }

    /// <summary>
    /// SDD-CT-06. tenants.slug is unique (IX_tenants_slug), and picking a name someone
    /// already took is the most likely mistake on the registration screen. Before this test
    /// the unique violation reached the handler as a raw DbUpdateException and came back as
    /// 500 server.unexpected, so a normal user error looked like the server falling over.
    /// </summary>
    [Fact]
    public async Task RegisterTenantRejectsASlugAlreadyTaken()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), true);
        var slug = NewSlug();

        using (var firstOwner = CreateOwnerClient(factory, NewEmail()))
        {
            var created = await RegisterAsync(firstOwner, slug);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        // A different person, the same slug: the collision is on the tenant, not on the
        // membership, so this isolates the index under test.
        using var secondOwner = CreateOwnerClient(factory, NewEmail());
        var response = await RegisterAsync(secondOwner, slug);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("tenancy.slug.taken", problem!.Code);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var count = await QueryRowAsync(
            connection,
            "SELECT count(*) FROM tenancy.tenants WHERE slug = @slug",
            ("slug", slug));
        Assert.Equal("1", count![0]);
    }

    private static string NewEmail() => $"owner-{Guid.NewGuid():N}@example.com";

    private static string NewSlug() => $"org-{Guid.NewGuid():N}"[..12];

    private static async Task<HttpResponseMessage> RegisterAsync(
        HttpClient client,
        string slug)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/auth/register-tenant")
        {
            Content = JsonContent.Create(new
            {
                displayName = "Acme Organization",
                slug,
                defaultCulture = "es-CO",
                timeZone = "America/Bogota",
                dateFormat = "yyyy-MM-dd",
            }),
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

    private static HttpClient CreateOwnerClient(QepApiFactory factory, string email)
    {
        var client = factory.CreateClient();
        // Owner has signed in with Google (simulated verified email via the dev stub).
        client.DefaultRequestHeaders.Add("X-Subject-Id", Guid.CreateVersion7().ToString());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", Guid.CreateVersion7().ToString());
        client.DefaultRequestHeaders.Add("X-Email", email);
        client.DefaultRequestHeaders.Add("X-Email-Verified", "true");
        return client;
    }

    private sealed record PolicyPayload(bool PublicTenantSignupEnabled);

    private sealed record RegisterPayload(Guid TenantId, Guid OwnerUserId);

    /// <summary>ProblemDetails extensions arrive flattened at the root (ApiExceptionHandler).</summary>
    private sealed record ProblemPayload(string Code);

    private sealed class QepApiFactory(string connectionString, bool signupEnabled)
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
            builder.UseSetting(
                "Registration:PublicTenantSignupEnabled",
                signupEnabled ? "true" : "false");
        }
    }
}
