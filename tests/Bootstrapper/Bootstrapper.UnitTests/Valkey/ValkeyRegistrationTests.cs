using System.Threading.RateLimiting;
using Bootstrapper.Valkey;
using Bootstrapper.Valkey.Permissions;
using Bootstrapper.Valkey.Presence;
using Bootstrapper.Valkey.RateLimiting;
using Bootstrapper.Valkey.Sessions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Modules.Authorization.Application;
using Modules.Buyers.Application;
using Modules.Identity.Application;
using Modules.Messaging.Application;
using Modules.Tenancy.Application;

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

    [Theory]
    [InlineData("true", typeof(ValkeyQueueDoorbell))]
    [InlineData("false", typeof(NullQueueDoorbell))]
    public void TheDoorbellFollowsItsArea(string on, Type expected)
    {
        var services = Register(("Valkey:Areas:Doorbells", on));
        using var provider = services.BuildServiceProvider();

        Assert.IsType(expected, provider.GetRequiredService<BuildingBlocks.Application.IQueueDoorbell>());
    }

    [Fact]
    public void WithRateLimitingOnThePartitionsUseValkey()
    {
        var services = Register(("Valkey:Areas:RateLimiting", "true"));
        using var provider = services.BuildServiceProvider();

        var partition = provider.GetRequiredService<SharedRateLimitPartitions>().FixedWindow(
            "public", "203.0.113.7", new FixedWindowRateLimiterOptions { PermitLimit = 1, Window = TimeSpan.FromMinutes(1) });

        using var limiter = partition.Factory(partition.PartitionKey);
        Assert.IsType<ValkeyFixedWindowRateLimiter>(limiter);
    }

    [Fact]
    public void WithRateLimitingOffThePartitionsStayInMemory()
    {
        var services = Register(("Valkey:Areas:RateLimiting", "false"));
        using var provider = services.BuildServiceProvider();

        var partition = provider.GetRequiredService<SharedRateLimitPartitions>().FixedWindow(
            "public", "203.0.113.7", new FixedWindowRateLimiterOptions { PermitLimit = 1, Window = TimeSpan.FromMinutes(1) });

        using var limiter = partition.Factory(partition.PartitionKey);
        Assert.IsType<FixedWindowRateLimiter>(limiter);
    }

    // D-4: la caché de sesión de Identity y la del comprador siguen el mismo interruptor.
    [Theory]
    [InlineData("true", typeof(ValkeySessionCache), typeof(ValkeyBuyerSessionCache))]
    [InlineData("false", typeof(NullSessionCache), typeof(NullBuyerSessionCache))]
    public void TheSessionCachesFollowTheSessionsArea(string on, Type identity, Type buyer)
    {
        var services = Register(("Valkey:Areas:Sessions", on));
        using var provider = services.BuildServiceProvider();

        Assert.IsType(identity, provider.GetRequiredService<ISessionCache>());
        Assert.IsType(buyer, provider.GetRequiredService<IBuyerSessionCache>());
    }

    // D-5: apagada, IAuthorizationService es AuthorizationService tal cual (V3); prendida, el decorador. Se mira el registro y no
    // la resolución: AuthorizationService necesita el resto de la composición.
    [Theory]
    [InlineData("true", typeof(ValkeyTenantAccessVersion), true)]
    [InlineData("false", typeof(NullTenantAccessVersion), false)]
    public void ThePermissionsCacheFollowsThePermissionsArea(string on, Type version, bool decorated)
    {
        var services = Register(("Valkey:Areas:Permissions", on));
        using var provider = services.BuildServiceProvider();

        Assert.IsType(version, provider.GetRequiredService<ITenantAccessVersion>());
        var descriptor = Assert.Single(services, service => service.ServiceType == typeof(IAuthorizationService));
        Assert.Equal(decorated, descriptor.ImplementationType == typeof(CachedAuthorizationService));
        Assert.Contains(services, service => service.ServiceType == typeof(AuthorizationService));
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
