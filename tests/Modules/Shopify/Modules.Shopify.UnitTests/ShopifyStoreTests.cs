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

    // Ruling S16: shop/redact llega 48 horas después de desinstalar, cuando la ruta de Integrations pudo haberse borrado con la
    // conexión; la tienda guarda su dominio para que el módulo la encuentre sin Integrations.
    [Fact]
    public void RecordShopDomainNormalizesAndDoesNotTouchTheVersion()
    {
        var store = ShopifyStore.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), LinkedAt);

        store.RecordShopDomain("  Mi-Tienda.MyShopify.com ");

        Assert.Equal("mi-tienda.myshopify.com", store.ShopDomain);
        // La versión es el If-Match del PUT de la configuración: anotar el dominio no puede invalidar lo que el usuario edita.
        Assert.Equal(1L, store.Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RecordShopDomainRejectsAnEmptyDomain(string domain)
    {
        var store = ShopifyStore.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), LinkedAt);

        Assert.Throws<ArgumentException>(() => store.RecordShopDomain(domain));
    }

    [Fact]
    public void RecordShopDomainRejectsADomainWiderThanItsColumn()
    {
        var store = ShopifyStore.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), LinkedAt);

        Assert.Throws<ArgumentException>(() => store.RecordShopDomain(new string('a', ShopifyStore.ShopDomainMaxLength + 1)));
    }

    // §6.1 y §7: el PUT de la tienda (tarea 16) la llena entera; la versión es su If-Match, así que sube.
    [Fact]
    public void ConfigureFillsTheStoreAndBumpsTheVersion()
    {
        var store = ShopifyStore.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), LinkedAt);
        var advisor = Guid.CreateVersion7();
        var classification = Guid.CreateVersion7();
        var billing = new ShopifyBillingAccount(Guid.CreateVersion7(), "Bancolombia", "123-456", "cop");

        store.Configure(advisor, billing, classification, " Cédula ", "Tipo", " nit ", advisor, LinkedAt.AddHours(1));

        Assert.True(store.IsConfigured);
        Assert.Equal((advisor, billing with { Currency = "COP" }, classification), (store.AdvisorMembershipId!.Value, store.Billing, store.DefaultClassificationId));
        Assert.Equal(("Cédula", "Tipo", "NIT"), (store.DocumentAttributeKey, store.DocumentTypeAttributeKey, store.DefaultIdentificationType));
        Assert.Equal((2L, LinkedAt.AddHours(1), (Guid?)advisor), (store.Version, store.UpdatedAt, store.UpdatedBy));
    }

    [Fact]
    public void ConfigureRejectsAnEmptyAttributeKey()
    {
        var store = ShopifyStore.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), LinkedAt);
        var billing = new ShopifyBillingAccount(Guid.CreateVersion7(), "Bancolombia", "123-456", "COP");

        Assert.Throws<ArgumentException>(
            () => store.Configure(Guid.CreateVersion7(), billing, null, " ", "Tipo", "CC", Guid.CreateVersion7(), LinkedAt));
        Assert.Equal(1L, store.Version);
    }
}
