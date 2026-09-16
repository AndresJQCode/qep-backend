using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

// Consulta de sólo lectura entre módulos para que otros (por ejemplo uno que aprovisiona un
// subdominio acotado al tenant) puedan resolver el slug de un tenant sin tomar dependencia
// de la persistencia de Tenancy ni repetir el slug en su propio esquema.
public interface ITenantDirectory
{
    Task<string?> GetSlugAsync(TenantId tenantId, CancellationToken cancellationToken);

    /// <summary>Nombre visible del tenant. Lo usa Notifications para nombrar la organización
    /// en el correo de invitación: se lee al entregar y no viaja en el evento, así que un tenant
    /// renombrado entre la invitación y el envío sale con el nombre vigente.</summary>
    Task<string?> GetDisplayNameAsync(TenantId tenantId, CancellationToken cancellationToken);

    Task<string?> GetTimeZoneAsync(TenantId tenantId, CancellationToken cancellationToken);
}
