using Modules.Integrations.Domain;
using Modules.Tenancy.Domain;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Plan 2026-10-21 (proveedores habilitados por tenant): Zenvia, además del módulo consumidor, necesita que el
/// operador lo habilite para el tenant. Los demás proveedores no cambian.
/// </summary>
public sealed class ProviderEnablementTests
{
    [Fact]
    public void ZenviaRequiresEnablementAndNoOtherProviderDoes()
    {
        Assert.Equal(
            ["zenvia"],
            IntegrationProviders.All.Where(provider => provider.RequiresTenantEnablement).Select(provider => provider.Key));
    }

    // Si una clave que exige habilitación no estuviera en Tenancy, el operador nunca podría habilitarla.
    [Fact]
    public void EveryProviderThatRequiresEnablementIsATenancyKey()
    {
        Assert.All(
            IntegrationProviders.All.Where(provider => provider.RequiresTenantEnablement),
            provider => Assert.Contains(provider.Key, TenantProviderKeys.All));
    }

    [Fact]
    public void IsEnabledForFollowsTheSet()
    {
        var none = new HashSet<string>(StringComparer.Ordinal);
        var zenvia = new HashSet<string>(StringComparer.Ordinal) { "zenvia" };

        // null = el stub de desarrollo: todo habilitado, como IsVisibleFor(null).
        Assert.True(IntegrationProviders.Zenvia.IsEnabledFor(null));
        Assert.False(IntegrationProviders.Zenvia.IsEnabledFor(none));
        Assert.True(IntegrationProviders.WhatsAppCloud.IsEnabledFor(none));
        Assert.True(IntegrationProviders.Zenvia.IsEnabledFor(zenvia));
    }
}
