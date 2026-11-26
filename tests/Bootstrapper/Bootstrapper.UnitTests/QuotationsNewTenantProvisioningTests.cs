using Modules.Quotations.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>Decisión del owner, 2026-11-23: el tenant recién nacido arranca con compra mínima de 1 unidad y sin montos mínimos.</summary>
public sealed class QuotationsNewTenantProvisioningTests
{
    [Fact]
    public async Task SavesOneMinimumUnitAndNoMinimumTotalsInsideTheTransaction()
    {
        var steps = new List<string>();
        var store = new RecordingStore(steps);
        var unitOfWork = new RecordingUnitOfWork(steps);
        var tenantId = TenantId.New();

        await new QuotationsNewTenantProvisioning(store, unitOfWork).ProvisionAsync(tenantId, TestContext.Current.CancellationToken);

        var (savedTenant, saved) = Assert.Single(store.Saved);
        Assert.Equal(tenantId.Value, savedTenant);
        Assert.Equal(1, saved.MinimumUnits);
        Assert.Empty(saved.MinimumTotals);
        Assert.Equal(["begin", "save-settings", "save-changes", "commit"], steps);
    }

    private sealed class RecordingStore(List<string> steps) : IQuotationSettingsStore
    {
        public List<(Guid TenantId, QuotationSettings Settings)> Saved { get; } = [];

        public Task SaveAsync(Guid tenantId, QuotationSettings settings, CancellationToken cancellationToken)
        {
            steps.Add("save-settings");
            Saved.Add((tenantId, settings));
            return Task.CompletedTask;
        }

        public Task<QuotationSettings> GetAsync(Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Guid?> GetFreightTaxRateIdAsync(Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetFreightTaxRateIdAsync(Guid tenantId, Guid? taxRateId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FreightChargeModeSettings> GetFreightChargeModesAsync(Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetFreightChargeModesAsync(Guid tenantId, FreightChargeModeSettings settings, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OrderSettings> GetOrderSettingsAsync(Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetOrderSettingsAsync(Guid tenantId, OrderSettings settings, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingUnitOfWork(List<string> steps) : IQuotationsUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            steps.Add("save-changes");
            return Task.FromResult(0);
        }

        public Task<IQuotationsTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
        {
            steps.Add("begin");
            return Task.FromResult<IQuotationsTransaction>(new Transaction(steps));
        }

        public void DiscardPendingChanges() => throw new NotSupportedException();
    }

    private sealed class Transaction(List<string> steps) : IQuotationsTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken)
        {
            steps.Add("commit");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
