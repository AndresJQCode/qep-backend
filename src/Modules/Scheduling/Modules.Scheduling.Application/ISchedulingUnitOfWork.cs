namespace Modules.Scheduling.Application;

/// <summary>Confirma los cambios de un caso de uso en una sola transacción. La implementación traduce los errores de base (la restricción
/// de exclusión de las sesiones, la concurrencia optimista) a las excepciones de aplicación: Application no referencia EF Core.</summary>
public interface ISchedulingUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
