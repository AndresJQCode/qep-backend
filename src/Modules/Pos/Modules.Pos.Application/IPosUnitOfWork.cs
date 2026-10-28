namespace Modules.Pos.Application;

/// <summary>
/// Unidad de trabajo del módulo. Además de guardar, abre la transacción en la que corre el
/// contador de números (spec, «Crear venta», paso 7) y deja limpiar el contexto después de un
/// choque al guardar, para releer sin arrastrar las entidades del intento fallido (paso 8).
/// </summary>
public interface IPosUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    Task<IPosTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>Revierte la transacción abierta, si la hay, y suelta todo lo rastreado.</summary>
    Task ResetAsync(CancellationToken cancellationToken);
}

public interface IPosTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}
