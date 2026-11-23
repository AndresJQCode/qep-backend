using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Hr.Application;

namespace Bootstrapper.Valkey.LegalParameters;

public static class LegalParameterCacheRegistration
{
    /// <summary>
    /// Envuelve el <see cref="ILegalParameterSource"/> que registró <c>AddHrInfrastructure</c> con la caché del área (spec 2026-11-22,
    /// «Caché»). Tiene que correr <b>después</b> de <c>AddHrInfrastructure</c>: <c>AddQepValkey</c> corre antes y su registro quedaría
    /// pisado. La implementación de Hr es <c>internal</c> y el Bootstrapper no la puede nombrar, así que se toma del descriptor
    /// previo, se quita y se construye por <see cref="ActivatorUtilities"/> como la base del decorador. Con el área prendida, la
    /// tabla vive en Valkey (versión compartida entre réplicas); apagada, en la memoria del pod con TTL de 60 s.
    /// </summary>
    public static IServiceCollection AddLegalParameterCache(this IServiceCollection services, IConfiguration configuration)
    {
        var descriptor = services.LastOrDefault(service => service.ServiceType == typeof(ILegalParameterSource))
            ?? throw new InvalidOperationException(
                "ILegalParameterSource is not registered: call AddHrInfrastructure before AddLegalParameterCache.");
        services.Remove(descriptor);

        ILegalParameterSource CreateInner(IServiceProvider provider) =>
            descriptor.ImplementationFactory is { } factory
                ? (ILegalParameterSource)factory(provider)
                : (ILegalParameterSource)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);

        var valkey = configuration.GetSection(ValkeyOptions.SectionName).Get<ValkeyOptions>() ?? new ValkeyOptions();
        if (valkey.Enabled && ValkeyAreas.IsOn(valkey.Areas, ValkeyArea.LegalParameters))
        {
            services.AddScoped<ILegalParameterSource>(provider => new ValkeyLegalParameterSource(
                CreateInner(provider), provider.GetRequiredService<ValkeyGateway>(), provider.GetRequiredService<TimeProvider>()));
            services.AddSingleton<ILegalParameterCacheVersion, ValkeyLegalParameterCacheVersion>();
            services.AddHostedService<LegalParameterCacheStartupBump>();
            return services;
        }

        services.AddSingleton<MemoryLegalParameterCache>();
        services.AddScoped<ILegalParameterSource>(provider => new MemoryLegalParameterSource(
            CreateInner(provider), provider.GetRequiredService<MemoryLegalParameterCache>()));
        services.AddSingleton<ILegalParameterCacheVersion, MemoryLegalParameterCacheVersion>();
        return services;
    }
}
