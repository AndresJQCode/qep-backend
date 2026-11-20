using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using static Modules.Tenancy.IntegrationTests.TenancyTestDatabase;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// Spec 2026-11-19 (onboarding por link) §3b: el servidor sugiere un slug libre a partir de la razón social. Si está tomado
/// o reservado, agrega <c>-2</c>, <c>-3</c>… hasta encontrar uno libre. Cableado de DI y lectura real de la base.
/// </summary>
public sealed class TenantSlugSuggestionsTests
{
    [Fact]
    public async Task ATakenSlugGetsTheNextSuffix()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await StartDatabaseAsync();
        using var factory = new SlugFactory(database.GetConnectionString());
        await using (var seed = factory.Services.CreateAsyncScope())
        {
            var dbContext = seed.ServiceProvider.GetRequiredService<TenancyDbContext>();
            AddTenant(dbContext, "ferreteria-central");
            AddTenant(dbContext, "ferreteria-central-2");
            await dbContext.SaveChangesAsync(ct);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var suggestions = scope.ServiceProvider.GetRequiredService<ITenantSlugSuggestions>();

        Assert.Equal("ferreteria-central-3", await suggestions.SuggestAsync("Ferretería Central", ct));
        Assert.Equal("ferreteria-norte", await suggestions.SuggestAsync("Ferretería Norte", ct));
    }

    [Fact]
    public async Task AReservedSlugGetsASuffix()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await StartDatabaseAsync();
        using var factory = new SlugFactory(database.GetConnectionString());

        await using var scope = factory.Services.CreateAsyncScope();
        var suggestions = scope.ServiceProvider.GetRequiredService<ITenantSlugSuggestions>();

        Assert.True(TenantSlugReservations.IsReserved("soporte"));
        Assert.Equal("soporte-2", await suggestions.SuggestAsync("Soporte", ct));
    }

    private static void AddTenant(TenancyDbContext dbContext, string slug) =>
        dbContext.Tenants.Add(Tenant.Create(
            TenantId.New(), slug, slug, "es-CO", "America/Bogota", "dd/MM/yyyy", MembershipId.New(),
            DateTimeOffset.UtcNow));

    private sealed class SlugFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            // Pinned, never inherited: the developer's user-secrets may turn the seed on.
            builder.UseSetting("Seed:Enabled", "false");
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
        }
    }
}
