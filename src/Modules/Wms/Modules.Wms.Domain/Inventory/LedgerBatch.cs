namespace Modules.Wms.Domain.Inventory;

/// <summary>Un lote de movimientos que se aplican juntos o ninguno, bajo una sola llave de idempotencia. Tope de 500 (RNF-07).</summary>
public sealed class LedgerBatch
{
    public const int MaxEntries = 500;

    public LedgerBatch(Guid tenantId, Guid batchId, string idempotencyKey, LedgerActor actor, IReadOnlyList<LedgerEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            throw new ArgumentException("A ledger batch needs at least one entry.", nameof(entries));
        }

        if (entries.Count > MaxEntries)
        {
            throw new WmsDomainException(WmsErrorCodes.BatchTooLarge, $"Un lote admite como máximo {MaxEntries} movimientos.");
        }

        TenantId = tenantId;
        BatchId = batchId;
        IdempotencyKey = idempotencyKey;
        Actor = actor;
        Entries = entries;
    }

    public Guid TenantId { get; }

    public Guid BatchId { get; }

    public string IdempotencyKey { get; }

    public LedgerActor Actor { get; }

    public IReadOnlyList<LedgerEntry> Entries { get; }
}
