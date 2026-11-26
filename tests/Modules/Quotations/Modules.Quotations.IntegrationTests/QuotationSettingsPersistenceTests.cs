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
public sealed class QuotationSettingsPersistenceTests(SharedQuotationsHost sharedHost) : IClassFixture<SharedQuotationsHost>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoInsertsOfTheSameSettingsRowEndInAConcurrencyConflict(bool minimumTotal)
    {
        var factory = await sharedHost.StartTestAsync();
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

    // Spec 2026-11-10 §6: sin fila, los valores por defecto; guardados, se leen igual.
    [Fact]
    public async Task OrderSettingsRoundTripAndDefaultWithoutRow()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory);
        using var _ = client;

        await using (var read = factory.Services.CreateAsyncScope())
        {
            Assert.Equal(
                OrderSettings.Default,
                await read.ServiceProvider.GetRequiredService<IQuotationSettingsStore>()
                    .GetOrderSettingsAsync(tenantId, TestContext.Current.CancellationToken));
        }

        await using (var write = factory.Services.CreateAsyncScope())
        {
            var unitOfWork = write.ServiceProvider.GetRequiredService<IQuotationsUnitOfWork>();
            await using var transaction = await unitOfWork.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await write.ServiceProvider.GetRequiredService<IQuotationSettingsStore>().SetOrderSettingsAsync(
                tenantId, new OrderSettings(true, false, true), TestContext.Current.CancellationToken);
            await unitOfWork.SaveChangesAsync(TestContext.Current.CancellationToken);
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        await using var reread = factory.Services.CreateAsyncScope();
        Assert.Equal(
            new OrderSettings(true, false, true),
            await reread.ServiceProvider.GetRequiredService<IQuotationSettingsStore>()
                .GetOrderSettingsAsync(tenantId, TestContext.Current.CancellationToken));
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
