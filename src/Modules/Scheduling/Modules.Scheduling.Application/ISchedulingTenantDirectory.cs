namespace Modules.Scheduling.Application;

/// <summary>
/// Puerto hacia el nombre del tenant: la sede principal que aprovisiona el primer acceso se llama como él (plan 2026-11-21, corrección 12).
/// Infrastructure no conoce a Tenancy, así que el nombre lo lee el caso de uso por este puerto y se lo entrega al aprovisionador.
/// </summary>
public interface ISchedulingTenantDirectory
{
    Task<string?> GetDisplayNameAsync(Guid tenantId, CancellationToken cancellationToken);
}
