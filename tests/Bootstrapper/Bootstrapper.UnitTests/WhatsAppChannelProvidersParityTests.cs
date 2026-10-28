using Modules.Integrations.Domain;
using Modules.Quotations.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-10-21 D17: Quotations no referencia Integrations, así que las claves de proveedor de sus canales son una
/// copia de las de Integrations. Esta prueba, que sí ve los dos módulos, es lo que las amarra.
/// </summary>
public sealed class WhatsAppChannelProvidersParityTests
{
    [Fact]
    public void TheQuotationsKeysAreTheIntegrationsKeys()
    {
        Assert.Equal(WhatsAppChannelProviders.Meta, IntegrationProviders.WhatsAppCloud.Key);
        Assert.Equal(WhatsAppChannelProviders.Zenvia, IntegrationProviders.Zenvia.Key);
    }
}
