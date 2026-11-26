namespace Modules.Wms.Domain.Warehousing;

/// <summary>Un código escaneable (§25). El valor es único por tenant; el índice lo frena y <c>WmsUnitOfWork</c> lo traduce.</summary>
public sealed class Identifier
{
    public const int ValueMaxLength = 128;

    private Identifier()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public IdentifierKind Kind { get; private set; }

    public IdentifierSymbology Symbology { get; private set; }

    public string Value { get; private set; } = string.Empty;

    public Guid TargetId { get; private set; }

    public Guid? ConversionId { get; private set; }

    public bool IsPrimary { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Identifier Create(
        Guid id,
        Guid tenantId,
        IdentifierKind kind,
        IdentifierSymbology symbology,
        string value,
        Guid targetId,
        Guid? conversionId,
        bool isPrimary,
        DateTimeOffset now)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > ValueMaxLength)
        {
            throw new WmsDomainException(WmsErrorCodes.IdentifierValueInvalid, "The identifier value must have 1 to 128 characters.");
        }

        return new Identifier
        {
            Id = id,
            TenantId = tenantId,
            Kind = kind,
            Symbology = symbology,
            Value = trimmed,
            TargetId = targetId,
            ConversionId = conversionId,
            IsPrimary = isPrimary,
            CreatedAt = now,
        };
    }

    /// <summary>Los identificadores que nacen con el perfil: el código de la variante como <c>Internal</c> y, si hay código de
    /// barras, ese como <c>Gtin</c> primario. Si ambos valen lo mismo (SKU = EAN, válido en Catalog) queda una sola fila, la
    /// <c>Gtin</c> primaria: dos filas con el mismo valor chocarían contra <c>IX_identifiers_tenant_value</c> y esa variante nunca
    /// podría tener perfil.</summary>
    public static IReadOnlyList<Identifier> ForVariant(Guid tenantId, Guid variantId, string code, string? barcode, DateTimeOffset now)
    {
        var identifiers = new List<Identifier>(2);
        var gtin = string.IsNullOrWhiteSpace(barcode) ? null : barcode;

        if (gtin is null || !string.Equals(code?.Trim(), gtin.Trim(), StringComparison.Ordinal))
        {
            identifiers.Add(Create(Guid.CreateVersion7(), tenantId, IdentifierKind.Variant, IdentifierSymbology.Internal, code!, variantId, null, false, now));
        }

        if (gtin is not null)
        {
            identifiers.Add(Create(Guid.CreateVersion7(), tenantId, IdentifierKind.Variant, IdentifierSymbology.Gtin, gtin, variantId, null, true, now));
        }

        return identifiers;
    }
}
