using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Testcontainers.PostgreSql;

namespace Modules.Catalog.IntegrationTests;

/// <summary>El valor de la UVT por país y año: el mapeo y la clave (country, year).</summary>
public sealed class TaxUnitValuePersistenceTests
{
    private static readonly Guid Operator = Guid.Parse("01900000-0000-7000-8000-00000000d001");
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ValuesRoundTripAndReplaceKeepsOneRowPerYear()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var token = TestContext.Current.CancellationToken;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ITaxUnitValueRepository>()
                .Add(TaxUnitValue.Create("CO", 2026, 49799m, Operator, Now, 2026));
            await scope.ServiceProvider.GetRequiredService<ICatalogUnitOfWork>().SaveChangesAsync(token);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<ITaxUnitValueRepository>();
            var found = await repository.FindAsync("CO", 2026, token);
            Assert.NotNull(found);
            found.Replace(52374.5m, Operator, Now.AddDays(1));
            await scope.ServiceProvider.GetRequiredService<ICatalogUnitOfWork>().SaveChangesAsync(token);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<ITaxUnitValueRepository>();
            var list = await repository.ListAsync("CO", token);

            var row = Assert.Single(list);
            Assert.Equal(2026, row.Year);
            Assert.Equal(52374.5m, row.Value);
            Assert.Equal(Operator, row.UpdatedBy);
            Assert.True(await repository.AnyAsync("CO", token));
            Assert.False(await repository.AnyAsync("ES", token));
        }
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
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
        }
    }
}
