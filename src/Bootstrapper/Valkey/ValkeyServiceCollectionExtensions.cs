using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Bootstrapper.Valkey.Presence;
using Bootstrapper.Valkey.RateLimiting;
using Bootstrapper.Valkey.Sessions;
using Modules.Buyers.Application;
using Modules.Identity.Application;
using Modules.Messaging.Application;

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
    /// La caché de sesión (D-4). Apagada, el nulo: nunca acierta y cada request lee Postgres como antes. Prendida
    /// (<c>Valkey:Areas:Sessions</c>), la entrada vive en <c>qep:sess:{hash}</c> (Identity) o <c>qep:sess:buyer:{hash}</c>
    /// (comprador) y toda revocación la saca.
    /// </summary>
    private static void AddSessionCaches(IServiceCollection services, bool shared)
    {
        if (!shared)
        {
            services.AddSingleton<ISessionCache, NullSessionCache>();
            services.AddSingleton<IBuyerSessionCache, NullBuyerSessionCache>();
            return;
        }

        services.AddSingleton<ISessionCache, ValkeySessionCache>();
        services.AddSingleton<IBuyerSessionCache, ValkeyBuyerSessionCache>();
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
        if (!shared)
        {
            services.AddSingleton<IMessagingPresence>(provider => provider.GetRequiredService<MessagingPresence>());
            services.AddSingleton<IMessagingPresenceReader, LocalMessagingPresenceReader>();
            return;
        }

        services.AddSingleton<IMessagingPresence, ValkeyMessagingPresence>();
        services.AddSingleton(provider => new LocalMessagingPresenceReader(provider.GetRequiredService<MessagingPresence>()));
        services.AddSingleton<IMessagingPresenceReader, ValkeyMessagingPresenceReader>();
        services.AddHostedService<PresenceHeartbeatWorker>();
    }
}
