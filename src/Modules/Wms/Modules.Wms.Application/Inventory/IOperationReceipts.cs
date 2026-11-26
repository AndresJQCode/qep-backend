using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.Application;

/// <summary>Los recibos de operaciones idempotentes (RNF-02). El recibo viaja en la transacción del handler: si el movimiento no
/// confirma, tampoco queda recibo.</summary>
public interface IOperationReceipts
{
    /// <summary>Sin rastreo: el filtro la vuelve a llamar después de una transacción deshecha y no debe ver la fila que quedó
    /// <c>Added</c> en el tracker.</summary>
    Task<OperationReceipt?> FindAsync(Guid tenantId, string key, CancellationToken cancellationToken);

    /// <summary>Agrega el recibo con la respuesta serializada, sin guardar; lo commitea el <c>SaveChangesAsync</c> del handler.
    /// <see cref="InvalidOperationException"/> si el scope no está activo.</summary>
    void Record<TResponse>(TResponse response);

    Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}
