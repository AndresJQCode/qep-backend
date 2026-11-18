namespace Modules.Suppliers.Application;

public interface ISuppliersUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Olvida todo lo pendiente del intento que falló (la fila que chocó con un índice y su auditoría). Lo usa
    /// <see cref="SupplierLinking"/> antes de releer: sin esto, el siguiente <see cref="SaveChangesAsync"/> volvería a mandar
    /// el insert que ya chocó.
    /// </summary>
    void DiscardPendingChanges();
}
