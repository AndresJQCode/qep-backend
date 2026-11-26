namespace Modules.Siigo.Domain;

/// <summary>De dónde salió un vínculo de producto: emparejado por código (<c>AutoCode</c>) o elegido por una persona (<c>Manual</c>).
/// Se guarda por nombre: no se reordenan.</summary>
public enum SiigoLinkOrigin
{
    AutoCode,
    Manual,
}

/// <summary>
/// Spec 2026-11-18 §5.2: la variante de QEP (<c>variant_id</c>) y el producto de Siigo que factura. El vínculo es por variante: en QEP el
/// código vive en la variante y cada una es un producto distinto en Siigo. Guarda el código, el id y el nombre del producto como foto
/// para la pantalla (la lista de Siigo no se copia). La llave es <c>(connection_id, variant_id)</c>.
/// </summary>
public sealed class SiigoVariantLink
{
    public const int CodeMaxLength = 100;
    public const int ProductIdMaxLength = 100;
    public const int NameMaxLength = 300;

    private SiigoVariantLink()
    {
    }

    internal SiigoVariantLink(
        Guid connectionId,
        Guid tenantId,
        Guid variantId,
        string siigoProductCode,
        string siigoProductId,
        string siigoProductName,
        SiigoLinkOrigin origin,
        Guid linkedBy,
        DateTimeOffset linkedAt)
    {
        ConnectionId = connectionId;
        TenantId = tenantId;
        VariantId = variantId;
        Replace(siigoProductCode, siigoProductId, siigoProductName, origin, linkedBy, linkedAt);
    }

    public Guid ConnectionId { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid VariantId { get; private set; }

    public string SiigoProductCode { get; private set; } = string.Empty;

    public string SiigoProductId { get; private set; } = string.Empty;

    public string SiigoProductName { get; private set; } = string.Empty;

    /// <summary>Membresía (no usuario) de quien vinculó.</summary>
    public Guid LinkedBy { get; private set; }

    public DateTimeOffset LinkedAt { get; private set; }

    public SiigoLinkOrigin Origin { get; private set; }

    internal void Replace(
        string siigoProductCode,
        string siigoProductId,
        string siigoProductName,
        SiigoLinkOrigin origin,
        Guid linkedBy,
        DateTimeOffset linkedAt)
    {
        SiigoProductCode = Fit(siigoProductCode, CodeMaxLength);
        SiigoProductId = Fit(siigoProductId, ProductIdMaxLength);
        SiigoProductName = Fit(siigoProductName, NameMaxLength);
        Origin = origin;
        LinkedBy = linkedBy;
        LinkedAt = linkedAt;
    }

    // El nombre es una foto para la pantalla: un nombre de Siigo más largo que la columna se recorta, no rompe el vínculo.
    private static string Fit(string value, int maxLength)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
