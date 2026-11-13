using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

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
        return services;
    }
}
