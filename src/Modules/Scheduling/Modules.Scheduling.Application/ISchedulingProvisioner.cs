namespace Modules.Scheduling.Application;

/// <summary>
/// Crea, en el primer acceso, lo que todo tenant necesita para usar la Agenda: la sede principal y las reglas de reserva por defecto
/// (spec 2026-11-21, «Aprovisionamiento»). Tenancy no avisa cuando se activa un módulo, así que no se puede hacer «al activar». Idempotente
/// y seguro con peticiones simultáneas; todo caso de uso lo invoca antes de leer.
/// </summary>
public interface ISchedulingProvisioner
{
    /// <param name="mainLocationName">El nombre de la sede principal si hay que crearla: el del tenant. Si ya existe, no se toca.</param>
    Task EnsureAsync(Guid tenantId, string mainLocationName, CancellationToken cancellationToken);
}
