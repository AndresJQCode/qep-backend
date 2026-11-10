namespace Modules.Store.Domain;

/// <summary>
/// Consentimiento del comprador invitado sobre un pedido de la tienda (D9): cuándo aceptó, desde qué IP y qué política regía. El de
/// una cuenta de comprador vive en <c>buyers.consents</c>. El plazo de conservación de esta tabla está pendiente del owner.
/// </summary>
public sealed class StoreOrderConsent
{
    private StoreOrderConsent()
    {
        Consent = null!;
    }

    public Guid OrderId { get; private set; }

    public Guid TenantId { get; private set; }

    public StoreConsent Consent { get; private set; }

    public static StoreOrderConsent Create(Guid orderId, Guid tenantId, StoreConsent consent) =>
        new() { OrderId = orderId, TenantId = tenantId, Consent = consent };
}
