namespace Modules.Quotations.Domain;

/// <summary>Fila hija de <see cref="Carrier"/>: un nombre o alias con su forma normalizada.
/// No tiene identidad fuera del agregado.</summary>
public sealed class CarrierNameEntry
{
    // EF Core materializa por acá.
    private CarrierNameEntry()
    {
        Value = string.Empty;
        NormalizedName = string.Empty;
    }

    internal CarrierNameEntry(Guid tenantId, CarrierNameKind kind, string value)
    {
        TenantId = tenantId;
        Kind = kind;
        Value = value;
        NormalizedName = CarrierName.Normalize(value);
    }

    public Guid TenantId { get; private set; }

    public CarrierNameKind Kind { get; private set; }

    public string Value { get; private set; }

    public string NormalizedName { get; private set; }
}
