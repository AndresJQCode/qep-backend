namespace Modules.Wms.Domain.Inventory;

/// <summary>Una unidad de medida del tenant. Las seis del sistema se siembran en el primer acceso (P-2).</summary>
public sealed class UnitOfMeasure
{
    public const int MinPrecision = 0;
    public const int MaxPrecision = 6;
    public const int NameMaxLength = 40;

    private UnitOfMeasure()
    {
    }

    public Guid TenantId { get; private set; }

    public UomCode Code { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public int Precision { get; private set; }

    public bool IsSystem { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static UnitOfMeasure Create(Guid tenantId, UomCode code, string name, int precision, bool isSystem, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(trimmed.Length, NameMaxLength);

        if (precision is < MinPrecision or > MaxPrecision)
        {
            throw new WmsDomainException(WmsErrorCodes.UomPrecisionInvalid, "The precision must be between 0 and 6.");
        }

        return new UnitOfMeasure
        {
            TenantId = tenantId,
            Code = code,
            Name = trimmed,
            Precision = precision,
            IsSystem = isSystem,
            CreatedAt = now,
        };
    }

    public static IReadOnlyList<UnitOfMeasure> SystemDefaults(Guid tenantId, DateTimeOffset now) =>
    [
        Create(tenantId, UomCode.Parse("UND"), "Unidad", 0, true, now),
        Create(tenantId, UomCode.Parse("KG"), "Kilogramo", 3, true, now),
        Create(tenantId, UomCode.Parse("G"), "Gramo", 0, true, now),
        Create(tenantId, UomCode.Parse("L"), "Litro", 3, true, now),
        Create(tenantId, UomCode.Parse("ML"), "Mililitro", 0, true, now),
        Create(tenantId, UomCode.Parse("M"), "Metro", 2, true, now),
    ];
}
