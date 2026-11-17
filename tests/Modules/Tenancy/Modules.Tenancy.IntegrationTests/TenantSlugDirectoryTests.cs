using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using static Modules.Tenancy.IntegrationTests.TenancyTestDatabase;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// El tenant por su slug, resuelto del host real (plan Soporte fase 3, F2): el cableado de DI y la lectura desde la base. El puerto
/// recibe el slug ya normalizado y no lo vuelve a normalizar; tampoco mira el estado del tenant.
/// </summary>
public sealed class TenantSlugDirectoryTests
{
    [Fact]
    public async Task FindsByNormalizedSlugAndNullForUnknown()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await StartDatabaseAsync();
        using var factory = new SlugFactory(database.GetConnectionString());
        var tenantId = new TenantId(Guid.CreateVersion7());
        await using (var seed = factory.Services.CreateAsyncScope())
        {
            var dbContext = seed.ServiceProvider.GetRequiredService<TenancyDbContext>();
            dbContext.Tenants.Add(Tenant.Create(
                tenantId, "Mi-Empresa ", "Mi Empresa", "es-CO", "America/Bogota", "yyyy-MM-dd", MembershipId.New(),
                DateTimeOffset.UtcNow));
            await dbContext.SaveChangesAsync(ct);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var port = scope.ServiceProvider.GetRequiredService<ITenantSlugDirectory>();

        Assert.Equal(tenantId, await port.FindIdBySlugAsync("mi-empresa", ct));
        Assert.Null(await port.FindIdBySlugAsync("otra-empresa", ct));
        // Sin normalizar no casa: normalizar es trabajo de quien llama.
        Assert.Null(await port.FindIdBySlugAsync("Mi-Empresa", ct));
    }

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
