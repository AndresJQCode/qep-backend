using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// Escritura del historial de la consola (spec 2026-10-08 §3). Sólo agrega: una fila del historial no
/// se edita ni se borra. Se commitea con <see cref="ITenancyUnitOfWork"/>, en la misma transacción que
/// las filas de módulos o el estado del tenant y su auditoría.
/// </summary>
public interface ITenantChangeRepository
{
    void Add(TenantChange change);
}
