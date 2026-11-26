using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// Lo que otros módulos le dejan listo a un tenant recién nacido (decisión del owner, 2026-11-23). Puede haber varias
/// implementaciones y corren en el orden en que se registran. Cada una corre después del commit del tenant, en la unidad de
/// trabajo del módulo dueño, así que no es atómica con el alta: si falla, el tenant ya existe y queda sin lo que ella le iba a
/// dejar; si la excepción sube, las que siguen no corren. Se llama una sola vez por tenant, sólo cuando el alta lo crea (un
/// reintento que lo encuentra no lo vuelve a llamar).
/// </summary>
public interface INewTenantProvisioning
{
    Task ProvisionAsync(TenantId tenantId, CancellationToken cancellationToken);
}
