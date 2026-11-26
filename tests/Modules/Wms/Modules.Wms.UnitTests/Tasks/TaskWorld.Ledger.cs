using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.UnitTests.Tasks;

/// <summary>El ledger, el recibo idempotente y la métrica de confirmación, en memoria.</summary>
internal sealed partial class TaskWorld
{
    public List<LedgerBatch> Posted { get; } = [];

    public List<object?> Receipts { get; } = [];

    public IInventoryLedger Ledger() => new FakeLedger(this);

    public IOperationReceipts ReceiptStore() => new FakeReceipts(this);

    public IWmsConfirmMetrics Metrics { get; } = new FakeMetrics();

    private sealed class FakeLedger(TaskWorld world) : IInventoryLedger
    {
        public Task<LedgerPosting> PostAsync(LedgerBatch batch, CancellationToken cancellationToken)
        {
            world.Log.Add("ledger");
            world.Posted.Add(batch);
            return Task.FromResult(new LedgerPosting(batch.BatchId, [.. batch.Entries.Select(_ => Guid.NewGuid())]));
        }
    }

    private sealed class FakeReceipts(TaskWorld world) : IOperationReceipts
    {
        public void Record<TResponse>(TResponse response)
        {
            world.Log.Add("receipt");
            world.Receipts.Add(response);
        }

        public Task<OperationReceipt?> FindAsync(Guid tenantId, string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeMetrics : IWmsConfirmMetrics
    {
        public void RecordConfirmDuration(double milliseconds)
        {
        }
    }
}
