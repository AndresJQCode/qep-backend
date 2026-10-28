using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// The safety net under QuotationSettingsStore's lock: should two writers ever insert the same
/// settings or total row (a writer that skipped the store), the unit of work answers 412 and not
/// a 500 with the constraint name inside. Same shape as
/// OrdersExportLayoutPersistenceTests.TwoFirstSavesForTheSameTenantEndInAConcurrencyConflict.
/// </summary>
public sealed class QuotationSettingsPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoInsertsOfTheSameSettingsRowEndInAConcurrencyConflict(bool minimumTotal)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory);
        using var _ = client;
        await using var first = factory.Services.CreateAsyncScope();
        await using var second = factory.Services.CreateAsyncScope();
        if (minimumTotal)
        {
            // The parent row first, so the clash is on PK_tenant_minimum_totals and not on it.
            var parent = first.ServiceProvider.GetRequiredService<QuotationsDbContext>();
            parent.TenantQuotationSettings.Add(new TenantQuotationSettingsRow { TenantId = tenantId, MinimumUnits = 6 });
            await parent.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Stage(first, tenantId, minimumTotal);
        Stage(second, tenantId, minimumTotal);
        await first.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            second.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal("concurrency.conflict", error.Code);
        Assert.Contains("quotation settings", error.Message, StringComparison.Ordinal);
    }

    private static void Stage(AsyncServiceScope scope, Guid tenantId, bool minimumTotal)
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        if (minimumTotal)
        {
            dbContext.TenantMinimumTotals.Add(
                new TenantMinimumTotalRow { TenantId = tenantId, Currency = "COP", Amount = 500_000m });
        }
        else
        {
            dbContext.TenantQuotationSettings.Add(new TenantQuotationSettingsRow { TenantId = tenantId, MinimumUnits = 6 });
        }
    }
}
