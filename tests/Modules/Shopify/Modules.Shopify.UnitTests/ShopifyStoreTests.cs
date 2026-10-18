using Modules.Shopify.Domain;

namespace Modules.Shopify.UnitTests;

/// <summary>Spec 2026-10-15 §6.1: la tienda nace vacía al primer contacto, con las claves del documento por defecto, y no está
/// configurada hasta que tenga asesor y cuenta de cobro.</summary>
public sealed class ShopifyStoreTests
{
    private static readonly DateTimeOffset LinkedAt = new(2026, 10, 18, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NewStoreIsNotConfiguredAndCarriesTheDefaults()
    {
        var connectionId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();

        var store = ShopifyStore.Create(connectionId, tenantId, LinkedAt);

        Assert.False(store.IsConfigured);
        Assert.Equal((connectionId, tenantId, (Guid?)null), (store.ConnectionId, store.TenantId, store.AdvisorMembershipId));
        Assert.Null(store.Billing);
        Assert.Equal(("Documento", "Tipo de documento", "CC"), (store.DocumentAttributeKey, store.DocumentTypeAttributeKey, store.DefaultIdentificationType));
        // D-S4: no se trae historial; la conciliación arranca en el momento del enlace.
        Assert.Equal(((DateTimeOffset?)LinkedAt, 1L, LinkedAt), (store.LastReconciledAt, store.Version, store.UpdatedAt));
    }
}
