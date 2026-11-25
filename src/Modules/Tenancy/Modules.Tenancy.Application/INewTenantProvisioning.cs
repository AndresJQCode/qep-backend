using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// Lo que otros módulos le dejan listo a un tenant recién nacido (decisión del owner, 2026-11-23): hoy, los ajustes de
/// cotizaciones. Corre después del commit del tenant, en la unidad de trabajo del módulo dueño, así que no es atómico con el
/// alta: si falla, el tenant ya existe y queda con los valores por defecto del módulo. Se llama una sola vez por tenant, sólo
/// cuando el alta lo crea (un reintento que lo encuentra no lo vuelve a llamar).
/// </summary>
public interface INewTenantProvisioning
{
    Task ProvisionAsync(TenantId tenantId, CancellationToken cancellationToken);
}
