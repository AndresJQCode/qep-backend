using BuildingBlocks.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// La moneda del tenant resuelta del host real: el cableado de DI y la lectura desde la base. Un
/// tenant que no existe es <c>tenancy.tenant.not_found</c>, no un default.
/// </summary>
public sealed class TenantDefaultCurrencyTests
{
    [Fact]
    public async Task ReturnsTheStoredCurrencyAndThrowsForAnUnknownTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = await SeedTenantAsync(factory, currency: "USD");

        await using var scope = factory.Services.CreateAsyncScope();
        var port = scope.ServiceProvider.GetRequiredService<ITenantDefaultCurrency>();

        Assert.Equal("USD", await port.GetAsync(tenantId, TestContext.Current.CancellationToken));
        var missing = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            port.GetAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
        Assert.Equal("tenancy.tenant.not_found", missing.Code);
    }

    private static async Task<Guid> SeedTenantAsync(QepApiFactory factory, string currency)
    {
        var tenantId = Guid.CreateVersion7();
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var tenant = Tenant.Create(
            new TenantId(tenantId),
            "qcode-demo",
            "QCode Demo",
            "es-CO",
            "America/Bogota",
            "yyyy-MM-dd",
            MembershipId.New(),
            DateTimeOffset.UtcNow);
        tenant.UpdateSettings(
            tenant.DisplayName,
            tenant.DefaultCulture,
            tenant.TimeZone,
            tenant.DateFormat,
            currency,
            tenant.NumberFormat,
            DateTimeOffset.UtcNow);
        dbContext.Tenants.Add(tenant);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return tenantId;
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
            // Fijado, nunca heredado: mismo criterio que TenantSettingsApiTests (SDD-CT-17).
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
        }
    }
}
