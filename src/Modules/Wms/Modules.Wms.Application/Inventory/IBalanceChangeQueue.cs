namespace Modules.Wms.Application;

public sealed record BalanceChangeKey(Guid TenantId, Guid OwnerId, Guid WarehouseId, Guid VariantId);

// CA1711: el analizador asume que «Queue» implica heredar de System.Collections.Queue; acá es el nombre de la tabla del spec.
#pragma warning disable CA1711
/// <summary>Qué saldos cambiaron, agregado por <c>(dueño, bodega, variante)</c>: de acá sale <c>wms.balance.changed.v1</c>.</summary>
public interface IBalanceChangeQueue
#pragma warning restore CA1711
{
    /// <summary>Corre en la transacción del llamador, como última escritura del lote, y deja las filas bloqueadas hasta el commit. Sin
    /// claves no ejecuta nada.</summary>
    Task EnqueueAsync(IReadOnlyCollection<BalanceChangeKey> keys, CancellationToken ct);

    /// <summary>Saca de la cola hasta <paramref name="max"/> claves, las más viejas primero. Es un <c>DELETE</c>: quien llama abre la
    /// transacción si quiere que un fallo posterior las devuelva.</summary>
    Task<IReadOnlyList<BalanceChangeKey>> DequeueAllAsync(int max, CancellationToken ct);
}
