using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure;

namespace Modules.Tenancy.UnitTests;

/// <summary>Spec 2026-10-07, «Alta por signup»: los seis de hoy o ninguno, según el interruptor.</summary>
public sealed class TenantModuleDefaultsTests
{
    [Fact]
    public void WithTheSwitchOnANewTenantGetsTheSixOfToday()
    {
        var defaults = new TenantModuleDefaults(
            Options.Create(new EntitlementsOptions { GrantDefaultModulesOnSignup = true }));

        Assert.Equal(TenantModuleKeys.DefaultForNewTenants, defaults.ForNewTenants);
    }

    [Fact]
    public void WithTheSwitchOffANewTenantGetsNothing()
    {
        var defaults = new TenantModuleDefaults(
            Options.Create(new EntitlementsOptions { GrantDefaultModulesOnSignup = false }));

        Assert.Empty(defaults.ForNewTenants);
    }

    // El default conserva el comportamiento de hoy: sin la sección, los seis. Se resuelve desde DI
    // (no un POCO nuevo) para probar el registro real.
    [Fact]
    public void WithoutTheSectionTheSwitchIsOn()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>());

        Assert.True(provider.GetRequiredService<IOptions<EntitlementsOptions>>().Value.GrantDefaultModulesOnSignup);
        Assert.Equal(
            TenantModuleKeys.DefaultForNewTenants,
            provider.GetRequiredService<ITenantModuleDefaults>().ForNewTenants);
    }

    [Fact]
    public void TheSwitchSetToFalseInConfigurationIsHonoredThroughDi()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Entitlements:GrantDefaultModulesOnSignup"] = "false",
        });

        Assert.Empty(provider.GetRequiredService<ITenantModuleDefaults>().ForNewTenants);
    }

    // Review Focus 3: un valor que no es booleano no puede llegar al primer signup. Se ejerce el
    // ValidateOnStart real: IStartupValidator es lo que el host corre al arrancar.
    [Fact]
    public void ANonBooleanSwitchFailsStartupValidation()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Entitlements:GrantDefaultModulesOnSignup"] = "si",
        });

        var validator = provider.GetRequiredService<IStartupValidator>();

        Assert.ThrowsAny<Exception>(validator.Validate);
    }

    [Fact]
    public void ABooleanSwitchPassesStartupValidation()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Entitlements:GrantDefaultModulesOnSignup"] = "false",
        });

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> values)
    {
        values["ConnectionStrings:QepDatabase"] = "Host=localhost;Database=unused";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddTenancyInfrastructure(configuration);
        return services.BuildServiceProvider();
    }
}
