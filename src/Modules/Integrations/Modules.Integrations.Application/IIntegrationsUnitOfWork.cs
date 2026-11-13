namespace Modules.Integrations.Application;

/// <summary>
/// Guarda la conexión, sus secretos, su auditoría y sus eventos en una sola transacción. La
/// implementación traduce el índice único del nombre y la concurrencia (spec 2026-10-08,
/// «Proyectos»), en Infrastructure.
/// </summary>
public interface IIntegrationsUnitOfWork
{
    /// <summary>Saves. With a transaction opened by <see cref="BeginTransactionAsync"/>, it also commits it: saving is committing, and
    /// the advisory locks taken inside it are released there.</summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>An explicit transaction, for a check that has to hold until the write commits (plan 2026-11-12, Task 6: the bot
    /// occupancy check of <see cref="DeleteConnectionHandler"/> takes the tenant's slot lock inside it). Disposing it without saving
    /// rolls it back and releases its locks.</summary>
    Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken cancellationToken);
}
