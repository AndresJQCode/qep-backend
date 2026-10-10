using Microsoft.Extensions.Caching.Memory;
using Modules.Messaging.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Messaging.Infrastructure.Webhook;

/// <summary>§8.2: ruta por <c>phone_number_id</c> y módulo del tenant, con caché por pod de 60 s (D-M18).
/// Pausar o borrar tarda hasta un minuto en reflejarse; aceptado (§13).</summary>
internal sealed class WebhookRouting(IMessagingConnectionDirectory directory, ITenantModules tenantModules, IMemoryCache cache)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    /// <summary>Sólo se cachea lo encontrado: un <c>null</c> cacheado descartaría por un minuto los mensajes
    /// de un número recién conectado, y el descarte no tiene vuelta atrás.</summary>
    public async Task<MessagingRoute?> FindRouteAsync(string phoneNumberId, CancellationToken cancellationToken)
    {
        var key = $"messaging:route:{phoneNumberId}";
        if (cache.TryGetValue(key, out MessagingRoute? cached) && cached is not null)
        {
            return cached;
        }

        var route = await directory.FindRouteAsync(phoneNumberId, cancellationToken);
        if (route is not null)
        {
            cache.Set(key, route, Ttl);
        }

        return route;
    }

    public Task<bool> IsModuleEnabledAsync(Guid tenantId, CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync($"messaging:module:{tenantId}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Ttl;
            var set = await tenantModules.FindAsync(tenantId, cancellationToken);
            // null = el stub: no bloquea, como TenantModuleGuard.
            return set is null || set.IsEnabled(TenantModuleKeys.Messaging);
        });
}
