using Modules.Messaging.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// <c>variables.empresa</c> de las respuestas rápidas (spec 2026-10-12 §3.3, D-Q5): <c>Tenant.DisplayName</c> por
/// <see cref="ITenantDirectory"/>, la misma lectura con la que Notifications nombra la organización en la invitación. Se lee en
/// cada <c>GET</c>: un tenant renombrado sale con el nombre vigente.
/// </summary>
internal sealed class MessagingTenantNames(ITenantDirectory tenantDirectory) : IMessagingTenantNames
{
    public Task<string?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
        tenantDirectory.GetDisplayNameAsync(new TenantId(tenantId), cancellationToken);
}
