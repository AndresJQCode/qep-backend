using Microsoft.Extensions.Caching.Memory;
using Modules.Automations.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Automations.Infrastructure.Engine;

/// <summary>
/// Ruling P2-R42: el módulo <c>automations</c> del tenant, con la caché por pod de 60 s de <c>WebhookRouting.IsModuleEnabledAsync</c>
/// (Mensajería, D-M18). El motor lo pregunta en cada unidad; sin caché sería una lectura de Tenancy por entrante.
/// </summary>
internal sealed class AutomationsModuleStatus(ITenantModules tenantModules, IMemoryCache cache) : IAutomationsModuleStatus
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    /// <summary>Internal para las pruebas que apagan el módulo con el host ya en marcha.</summary>
    internal static string CacheKey(Guid tenantId) => $"automations:module:{tenantId}";

    public Task<bool> IsEnabledAsync(Guid tenantId, CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync(CacheKey(tenantId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Ttl;
            var set = await tenantModules.FindAsync(tenantId, cancellationToken);
            // null = el stub de desarrollo: no bloquea, como TenantModuleGuard y la ingesta de Mensajería.
            return set is null || set.IsEnabled(TenantModuleKeys.Automations);
        });
}
