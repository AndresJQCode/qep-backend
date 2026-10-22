using Modules.Automations.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>Spec 2026-10-21 §5.3: <c>{empresa}</c> en los mensajes del bot es <c>Tenant.DisplayName</c>, la misma lectura de
/// <see cref="MessagingTenantNames"/> para las respuestas rápidas. Se lee en cada corrida: un tenant renombrado sale con el nombre vigente.</summary>
internal sealed class AutomationTenantNames(ITenantDirectory tenantDirectory) : IAutomationTenantNames
{
    public Task<string?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
        tenantDirectory.GetDisplayNameAsync(new TenantId(tenantId), cancellationToken);
}
