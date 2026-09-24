using BuildingBlocks.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// El reloj del tenant resuelto del host real (spec 2026-09-17): <c>TenantClock</c> es internal, y
/// lo que importa verificar es el cableado —huso desde la base, instante desde <see cref="IClock"/>—
/// y que no haya default a UTC cuando el tenant no existe (decisión 3).
/// </summary>
public sealed class TenantClockTests
{
    // El tenant que TenancyDatabaseInitializer siembra en Development, con America/Bogota.
    private static readonly Guid DevelopmentTenantId = Guid.Parse("01900000-0000-7000-8000-000000000001");

    private static readonly DateTimeOffset NewYearsEveInBogota = new(2027, 1, 1, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AnUnknownTenantHasNoCalendar()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await using var scope = factory.Services.CreateAsyncScope();
        var tenantClock = scope.ServiceProvider.GetRequiredService<ITenantClock>();

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            tenantClock.GetAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.tenant.not_found", error.Code);
    }

    [Fact]
    public async Task TheCalendarTakesTheTenantsTimeZoneAndTheInjectedClock()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), NewYearsEveInBogota);
        await SeedDevelopmentTenantAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var tenantClock = scope.ServiceProvider.GetRequiredService<ITenantClock>();

        var calendar = await tenantClock.GetAsync(DevelopmentTenantId, TestContext.Current.CancellationToken);

        Assert.Equal("America/Bogota", calendar.TimeZone.Id);
        Assert.Equal(NewYearsEveInBogota, calendar.UtcNow);
        Assert.Equal(new DateOnly(2026, 12, 31), calendar.Today);
    }

    /// <summary>
    /// Desde el 2026-09-21 <c>TenancyDatabaseInitializer</c> ya no siembra ningún tenant: esta
    /// prueba tiene que crear el suyo con America/Bogota. El <c>ownerMembershipId</c> es nuevo y
    /// nunca se persiste como membership, así que no hace falta un usuario dueño real.
    /// </summary>
    private static async Task SeedDevelopmentTenantAsync(QepApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        dbContext.Tenants.Add(Tenant.Create(
            new TenantId(DevelopmentTenantId),
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

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class QepApiFactory(string connectionString, DateTimeOffset? utcNow = null)
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
            if (utcNow is { } fixedNow)
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IClock>();
                    services.AddScoped<IClock>(_ => new FixedClock(fixedNow));
                });
            }
        }
    }
}
