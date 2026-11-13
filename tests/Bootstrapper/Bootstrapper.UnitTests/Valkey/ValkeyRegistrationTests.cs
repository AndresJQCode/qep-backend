using Bootstrapper.Valkey;
using Bootstrapper.Valkey.Presence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Modules.Messaging.Application;

namespace Bootstrapper.UnitTests.Valkey;

/// <summary>
/// Qué registra <c>AddQepValkey</c> con un área prendida (V3 del plan: lo decide la configuración al arrancar). Sólo el
/// contenedor de DI, sin levantar el host ni conectar a Valkey: el cliente conecta en <c>StartAsync</c>, que acá no corre.
/// </summary>
public sealed class ValkeyRegistrationTests
{
    [Fact]
    public void WithPresenceOnTheSharedPresenceIsRegistered()
    {
        var services = Register(("Valkey:Areas:Presence", "true"));
        using var provider = services.BuildServiceProvider();

        Assert.IsType<ValkeyMessagingPresence>(provider.GetRequiredService<IMessagingPresence>());
        Assert.IsType<ValkeyMessagingPresenceReader>(provider.GetRequiredService<IMessagingPresenceReader>());
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(PresenceHeartbeatWorker));
    }

    internal static ServiceCollection Register(params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string?> { ["Valkey:Enabled"] = "true" };
        foreach (var (key, value) in settings)
        {
            values[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new DevelopmentEnvironment());
        services.AddLogging();
        services.AddMetrics();
        services.AddQepValkey(configuration);
        return services;
    }

    // El validador de ValkeyOptions pide el ambiente; en Development no exige la cadena.
    private sealed class DevelopmentEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Bootstrapper.UnitTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
