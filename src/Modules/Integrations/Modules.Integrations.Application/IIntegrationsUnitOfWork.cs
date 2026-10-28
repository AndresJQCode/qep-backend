namespace Modules.Integrations.Application;

/// <summary>
/// Guarda la conexión, sus secretos, su auditoría y sus eventos en una sola transacción. La
/// implementación traduce el índice único del nombre y la concurrencia (spec 2026-10-08,
/// «Proyectos»), en Infrastructure.
/// </summary>
public interface IIntegrationsUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
