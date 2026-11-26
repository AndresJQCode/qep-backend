using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.Application;

public sealed record LedgerPosting(Guid BatchId, IReadOnlyList<Guid> MovementIds);

/// <summary>
/// El único escritor de movimientos y saldos (RF-020, ADR-03). Exige la transacción de <see cref="IWmsUnitOfWork.BeginTransactionAsync"/>
/// y <b>no commitea</b>: los saldos ya quedaron escritos en esa transacción y los movimientos rastreados, y el handler cierra todo
/// junto con <see cref="IWmsUnitOfWork.SaveChangesAsync"/>.
/// <para>Una sola llamada por transacción: dos lotes tomarían bloqueos de la cola antes que los de saldo del segundo, y el orden global
/// que evita los deadlocks deja de valer. Lo que deba aplicarse junto va en un solo <see cref="LedgerBatch"/>.</para>
/// <para>El dueño lo valida quien llama: el ledger no lo busca, y la FK no incluye el tenant.</para>
/// </summary>
public interface IInventoryLedger
{
    Task<LedgerPosting> PostAsync(LedgerBatch batch, CancellationToken cancellationToken);
}
