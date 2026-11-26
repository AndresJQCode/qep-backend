using Bootstrapper.Realtime;
using Bootstrapper.Valkey.Permissions;
using Bootstrapper.Valkey.Presence;
using Bootstrapper.Valkey.RateLimiting;
using Bootstrapper.Valkey.Sessions;
using BuildingBlocks.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Modules.ApiAccess.Application;
using Modules.Authorization.Application;
using Modules.Buyers.Application;
using Modules.Identity.Application;
using Modules.Messaging.Application;
using Modules.Tenancy.Application;

namespace Bootstrapper.Valkey;

public static class ValkeyServiceCollectionExtensions
{
    /// <summary>
    /// El cliente de Valkey (spec 2026-11-13, D-1): opciones con su validador, el multiplexer como hosted service, las
    /// métricas y el gateway. Qué implementación usa cada área lo decide su propio registro al arrancar (V3 del plan): con
    /// el área apagada se registran exactamente las clases de antes.
    /// </summary>
    public static IServiceCollection AddQepValkey(this IServiceCollection services, IConfiguration configuration)
    {
        // En Production la cadena es obligatoria aunque todo esté apagado (ValkeyOptionsValidator).
        services.AddOptions<ValkeyOptions>()
            .Bind(configuration.GetSection(ValkeyOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ValkeyOptions>, ValkeyOptionsValidator>();

        services.AddSingleton<ValkeyConnection>();
        services.AddSingleton<ValkeyMetrics>();
        services.AddSingleton<ValkeyGateway>();
        services.TryAddSingleton(TimeProvider.System);

        // Las métricas se resuelven al arrancar y no cuando la pide el primer adaptador: el paso 2 del despliegue verifica
        // qep_valkey_connected = 1 con todas las áreas apagadas, y sin esto el gauge no existiría.
        services.AddHostedService(provider =>
        {
            _ = provider.GetRequiredService<ValkeyMetrics>();
            return provider.GetRequiredService<ValkeyConnection>();
        });

        var valkey = configuration.GetSection(ValkeyOptions.SectionName).Get<ValkeyOptions>() ?? new ValkeyOptions();
        AddPresence(services, IsOn(valkey, ValkeyArea.Presence));
        AddSessionCaches(services, IsOn(valkey, ValkeyArea.Sessions));
        AddPermissionsCache(services, IsOn(valkey, ValkeyArea.Permissions));

        AddDoorbells(services, IsOn(valkey, ValkeyArea.Doorbells));
        AddApiKeyTouchGate(services, IsOn(valkey, ValkeyArea.ApiKeyTouch));

        // El líder del muestreador de colas (D-7, plan P14): se registra siempre y decide en cada vuelta con el
        // interruptor de Doorbells; apagado, todos los pods muestrean como antes.
        services.AddSingleton<QueueSamplerLeadership>();

        // Los limitadores (D-3): la fábrica decide memoria o Valkey con el interruptor del área, y apagada devuelve
        // exactamente la partición de memoria de antes.
        services.AddSingleton(provider => new SharedRateLimitPartitions(
            provider.GetRequiredService<ValkeyGateway>(), provider.GetRequiredService<TimeProvider>()));
        return services;
    }

    // Lo que se registra se decide con la configuración al arrancar (V3 del plan): un área cuenta prendida sólo con el
    // interruptor general también prendido, igual que en ValkeyGateway.IsEnabled.
    private static bool IsOn(ValkeyOptions valkey, ValkeyArea area) => valkey.Enabled && ValkeyAreas.IsOn(valkey.Areas, area);

    /// <summary>
    /// Los timbres de las colas (D-7). Apagados, el nulo: nadie publica y cada worker espera su intervalo de hoy (plan P9).
    /// Prendidos (<c>Valkey:Areas:Doorbells</c>), los productores publican en <c>qep:wake:{cola}</c> después del commit y los
    /// workers esperan hasta su red de seguridad o el primer aviso.
    /// </summary>
    private static void AddDoorbells(IServiceCollection services, bool shared)
    {
        if (!shared)
        {
            services.AddSingleton<IQueueDoorbell, NullQueueDoorbell>();
            return;
        }

        services.AddSingleton<IQueueDoorbell, ValkeyQueueDoorbell>();
    }

    /// <summary>
    /// La compuerta del toque de <c>last_used_at</c> de las claves de API (D-8). Apagada, el nulo: cada request intenta su
    /// <c>UPDATE</c> como antes. Prendida (<c>Valkey:Areas:ApiKeyTouch</c>), sólo el que pone <c>qep:touch:apikey:{id}</c>.
    /// </summary>
    private static void AddApiKeyTouchGate(IServiceCollection services, bool shared)
    {
        if (!shared)
        {
            services.AddSingleton<IApiKeyTouchGate, NullApiKeyTouchGate>();
            return;
        }

        services.AddSingleton<IApiKeyTouchGate, ValkeyApiKeyTouchGate>();
    }

    /// <summary>
    /// La caché de sesión (D-4). Apagada, la de Identity es la del pod (<see cref="MemorySessionCache"/>: mismos TTL y marca de
    /// revocación, sin compartir entre pods) y la del comprador el nulo, que nunca acierta. Prendida
    /// (<c>Valkey:Areas:Sessions</c>), la entrada vive en <c>qep:sess:{hash}</c> (Identity) o <c>qep:sess:buyer:{hash}</c>
    /// (comprador) y toda revocación la saca.
    /// </summary>
    private static void AddSessionCaches(IServiceCollection services, bool shared)
    {
        if (!shared)
        {
            services.AddSingleton<ISessionCache, MemorySessionCache>();
            services.AddSingleton<IBuyerSessionCache, NullBuyerSessionCache>();
            return;
        }

        services.AddSingleton<ISessionCache, ValkeySessionCache>();
        services.AddSingleton<IBuyerSessionCache, ValkeyBuyerSessionCache>();
    }

    /// <summary>
    /// La caché de permisos (D-5; plan P5). <see cref="AuthorizationService"/> se registra siempre como clase concreta (scoped,
    /// como antes). Apagada, <see cref="MemoryCachedAuthorizationService"/> la decora con la caché del pod y la versión de acceso
    /// del tenant es el contador de ese mismo pod (<see cref="MemoryPermissionCache"/>). Prendida
    /// (<c>Valkey:Areas:Permissions</c>), <see cref="CachedAuthorizationService"/> la decora y los casos de uso que cambian
    /// permisos suben <c>qep:tenantver:{tenantId}</c>.
    /// </summary>
    private static void AddPermissionsCache(IServiceCollection services, bool shared)
    {
        services.AddScoped<AuthorizationService>();
        if (!shared)
        {
            services.AddSingleton<MemoryPermissionCache>();
            services.AddScoped<IAuthorizationService, MemoryCachedAuthorizationService>();
            services.AddSingleton<ITenantAccessVersion, MemoryTenantAccessVersion>();
            return;
        }

        services.AddScoped<IAuthorizationService, CachedAuthorizationService>();
        services.AddSingleton<ITenantAccessVersion, ValkeyTenantAccessVersion>();
    }

    /// <summary>
    /// La presencia de la bandeja (D-2). Siempre hay una sola instancia de <see cref="MessagingPresence"/>, el registro local
    /// del pod. Apagada, es el <see cref="IMessagingPresence"/> de <c>MessagingHub</c> y el lector la lee directo, como antes:
    /// el reporte ve sólo a los conectados a este pod. Prendida (<c>Valkey:Areas:Presence</c>), el hub escribe además en
    /// <c>qep:presence:{tenantId}</c>, un latido lo mantiene cada 30 s y el lector ve los de todos los pods, con el registro
    /// local de respaldo si Valkey falla.
    /// </summary>
    private static void AddPresence(IServiceCollection services, bool shared)
    {
        services.AddSingleton<MessagingPresence>();
        // The app hub's «está respondiendo» (spec 2026-11-15 §5.3, plan N11): the same area. The pod-local registry always exists,
        // as the registry when the area is off and as the fallback of the shared one when Valkey fails.
        services.AddSingleton<LocalRealtimePresence>();
        // Spec 2026-11-22 D-1: «has QEP open», read by the task notice worker. Local registry always; shared with the area on.
        services.AddSingleton<LocalAppMemberPresence>();
        if (!shared)
        {
            services.AddSingleton<IAppMemberPresence>(provider => provider.GetRequiredService<LocalAppMemberPresence>());
            services.AddSingleton<IMessagingPresence>(provider => provider.GetRequiredService<MessagingPresence>());
            services.AddSingleton<IMessagingPresenceReader, LocalMessagingPresenceReader>();
            services.AddSingleton<IRealtimePresence>(provider => provider.GetRequiredService<LocalRealtimePresence>());
            return;
        }

        services.AddSingleton<IRealtimePresence, ValkeyRealtimePresence>();
        services.AddSingleton<IAppMemberPresence, ValkeyAppMemberPresence>();
        services.AddHostedService<AppPresenceHeartbeatWorker>();
        services.AddSingleton<IMessagingPresence, ValkeyMessagingPresence>();
        services.AddSingleton(provider => new LocalMessagingPresenceReader(provider.GetRequiredService<MessagingPresence>()));
        services.AddSingleton<IMessagingPresenceReader, ValkeyMessagingPresenceReader>();
        services.AddHostedService<PresenceHeartbeatWorker>();
    }
}
