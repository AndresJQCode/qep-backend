using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

public sealed class PriceRoundingSettingsApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-000000000001";
    private const string SubjectId = "01900000-0000-7000-8000-000000000002";
    private const string OtherTenantId = "01900000-0000-7000-8000-0000000000ff";
    private const string OtherSubjectId = "01900000-0000-7000-8000-0000000000fe";

    private static string Url => $"/api/v1/tenants/{TenantId}/settings/price-rounding";

    [Fact]
    public async Task GetListsEveryCatalogueCurrencyWithItsDefault()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateClient(factory, SubjectId, TenantId);

        var body = await client.GetFromJsonAsync<JsonElement>(Url, TestContext.Current.CancellationToken);

        var currencies = body.GetProperty("currencies");
        Assert.Equal(3, currencies.GetArrayLength());
        AssertRow(currencies[0], "COP", 0, "Nearest", 0, true);
        AssertRow(currencies[1], "USD", 2, "Nearest", 2, true);
        AssertRow(currencies[2], "EUR", 2, "Nearest", 2, true);
    }

    [Fact]
    public async Task PutReturnsTheWholeListAndGetReadsItBack()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateClient(factory, SubjectId, TenantId);

        var put = await PutAsync(client, Body(("USD", 2, "Up")));

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var returned = (await put.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("currencies");
        Assert.Equal(3, returned.GetArrayLength());
        AssertRow(returned[1], "USD", 2, "Up", 2, false);

        var read = (await client.GetFromJsonAsync<JsonElement>(Url, TestContext.Current.CancellationToken))
            .GetProperty("currencies");
        AssertRow(read[0], "COP", 0, "Nearest", 0, true);
        AssertRow(read[1], "USD", 2, "Up", 2, false);
        AssertRow(read[2], "EUR", 2, "Nearest", 2, true);
    }

    [Theory]
    [InlineData("USD", 3, "Up", "Currencies[1].Decimals")]
    [InlineData("USD", -1, "Up", "Currencies[1].Decimals")]
    [InlineData("USD", 2, "Ceiling", "Currencies[1].Mode")]
    public async Task PutRejectsAnInvalidRuleWithItsField(string currency, int decimals, string mode, string field)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateClient(factory, SubjectId, TenantId);

        var response = await PutAsync(client, Body((currency, decimals, mode)));

        await AssertValidationErrorAsync(response, field);
    }

    [Fact]
    public async Task PutWithoutAllCatalogueCurrenciesIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateClient(factory, SubjectId, TenantId);

        var response = await PutAsync(client, new
        {
            currencies = new[]
            {
                new { currency = "COP", decimals = 0, mode = "Nearest" },
                new { currency = "USD", decimals = 2, mode = "Up" },
            },
        });

        await AssertValidationErrorAsync(response, "Currencies");
    }

    [Fact]
    public async Task PutWithAnUnknownOrRepeatedCurrencyIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateClient(factory, SubjectId, TenantId);

        var unknown = await PutAsync(client, new
        {
            currencies = new[]
            {
                new { currency = "COP", decimals = 0, mode = "Nearest" },
                new { currency = "USD", decimals = 2, mode = "Nearest" },
                new { currency = "XXX", decimals = 2, mode = "Nearest" },
            },
        });
        var repeated = await PutAsync(client, new
        {
            currencies = new[]
            {
                new { currency = "COP", decimals = 0, mode = "Nearest" },
                new { currency = "USD", decimals = 2, mode = "Nearest" },
                new { currency = "USD", decimals = 2, mode = "Up" },
            },
        });

        await AssertValidationErrorAsync(unknown, "Currencies");
        await AssertValidationErrorAsync(repeated, "Currencies");
    }

    [Fact]
    public async Task PutWithoutCurrenciesIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateClient(factory, SubjectId, TenantId);
        (await PutAsync(client, Body(("USD", 2, "Up")))).EnsureSuccessStatusCode();

        var response = await PutAsync(client, new { });

        await AssertValidationErrorAsync(response, "Currencies");
        var read = (await client.GetFromJsonAsync<JsonElement>(Url, TestContext.Current.CancellationToken))
            .GetProperty("currencies");
        AssertRow(read[1], "USD", 2, "Up", 2, false);
    }

    [Fact]
    public async Task ReadingAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateClient(factory, OtherSubjectId, OtherTenantId);

        var get = await client.GetAsync(Url, TestContext.Current.CancellationToken);
        var put = await PutAsync(client, Body(("USD", 2, "Up")));

        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }

    [Fact]
    public async Task TheChangeIsAuditedInTheSameTransaction()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory);
        using var client = CreateClient(factory, SubjectId, TenantId);

        (await PutAsync(client, Body(("USD", 2, "Up")))).EnsureSuccessStatusCode();

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT actor_id::text, outcome, changed_fields::text,
                   (SELECT count(*) FROM tenancy.tenant_price_roundings WHERE tenant_id = @tenantId)
            FROM audit.entries
            WHERE tenant_id = @tenantId AND action = 'tenancy.price_rounding.updated'
            """,
            connection);
        command.Parameters.AddWithValue("tenantId", Guid.Parse(TenantId));
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(SubjectId, reader.GetString(0));
        Assert.Equal("success", reader.GetString(1));
        Assert.Contains("USD", reader.GetString(2), StringComparison.Ordinal);
        Assert.Equal(1L, reader.GetInt64(3));
        Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    private static void AssertRow(
        JsonElement row, string currency, int decimals, string mode, int maxDecimals, bool isDefault)
    {
        Assert.Equal(currency, row.GetProperty("currency").GetString());
        Assert.Equal(decimals, row.GetProperty("decimals").GetInt32());
        Assert.Equal(mode, row.GetProperty("mode").GetString());
        Assert.Equal(maxDecimals, row.GetProperty("maxDecimals").GetInt32());
        Assert.Equal(isDefault, row.GetProperty("isDefault").GetBoolean());
    }

    private static async Task AssertValidationErrorAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("validation.failed", problem.GetProperty("code").GetString());
        Assert.True(
            problem.GetProperty("errors").TryGetProperty(field, out _),
            $"Expected an error for '{field}': {problem.GetProperty("errors")}");
    }

    // El catálogo completo; lo que la prueba no menciona queda en su valor por defecto.
    private static object Body(params (string Currency, int Decimals, string Mode)[] changes) => new
    {
        currencies = new[]
        {
            Pick(changes, "COP", 0),
            Pick(changes, "USD", 2),
            Pick(changes, "EUR", 2),
        },
    };

    private static object Pick((string Currency, int Decimals, string Mode)[] changes, string code, int defaultDecimals)
    {
        var change = changes.FirstOrDefault(candidate => candidate.Currency == code);
        return change.Currency is null
            ? new { currency = code, decimals = defaultDecimals, mode = "Nearest" }
            : new { currency = code, decimals = change.Decimals, mode = change.Mode };
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, object body) =>
        client.PutAsJsonAsync(Url, body, TestContext.Current.CancellationToken);

    private static async Task SeedTenantAsync(QepApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        dbContext.Tenants.Add(Tenant.Create(
            new Modules.Tenancy.Domain.TenantId(Guid.Parse(TenantId)),
            "qcode-demo",
            "QCode Demo",
            "es-CO",
            "America/Bogota",
            "yyyy-MM-dd",
            MembershipId.New(),
            DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
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

    private static HttpClient CreateClient(QepApiFactory factory, string subjectId, string tenantId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        return client;
    }

    private sealed class QepApiFactory(string connectionString) : WebApplicationFactory<Program>
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
            // Fijado, no heredado: ver QepApiFactory en TenantSettingsApiTests.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
        }
    }
}
