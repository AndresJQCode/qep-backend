using BuildingBlocks.Domain.Catalog;
using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.Domain.Warehousing;

/// <summary>
/// Perfil logístico de una variante (RF-006 a RF-010): unidad base, rastreo, medidas y presentaciones. La comprobación
/// «precisión del perfil ≤ precisión de la UoM» (<c>wms.uom.precision_exceeded</c>) la hace el handler, que tiene el catálogo.
/// </summary>
public sealed class VariantLogisticsProfile
{
    private readonly List<UomConversion> _conversions = [];

    private VariantLogisticsProfile()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid VariantId { get; private set; }

    public Guid ProductId { get; private set; }

    public UomCode BaseUom { get; private set; }

    public int BaseUomPrecision { get; private set; }

    public TrackingPolicy Tracking { get; private set; }

    public bool TracksExpiry { get; private set; }

    public decimal? WeightKg { get; private set; }

    public decimal? VolumeM3 { get; private set; }

    public decimal? LengthCm { get; private set; }

    public decimal? WidthCm { get; private set; }

    public decimal? HeightCm { get; private set; }

    public string? StorageClass { get; private set; }

    public UomCode? DisplayUom { get; private set; }

    public IReadOnlyList<UomConversion> Conversions => _conversions;

    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>La precisión del perfil por defecto según la unidad de venta de Catalog (inventario básico §5.6): 2 decimales para lo
    /// que se vende por peso, longitud, área o volumen; 0 para el resto. Las cantidades de pedidos, POS y compras traen a lo sumo 2.</summary>
    public static int DefaultPrecisionFor(SalesUnit salesUnit) => salesUnit switch
    {
        SalesUnit.Kilogram or SalesUnit.Meter or SalesUnit.SquareMeter or SalesUnit.Liter => 2,
        _ => 0,
    };

    /// <summary>ADR-07 y ADR-08: <c>UND</c>, sin rastreo, y una presentación «EMPAQUE x{n}» por cada <c>PackagingUnit</c> de la
    /// variante. La precisión la pone quien llama (<see cref="DefaultPrecisionFor"/>): 0 si no sabe la unidad de venta.</summary>
    public static VariantLogisticsProfile CreateDefault(
        Guid tenantId,
        Guid variantId,
        Guid productId,
        IReadOnlyList<int> packagingUnits,
        int baseUomPrecision,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(packagingUnits);

        var conversions = packagingUnits
            .Where(n => n > 0)
            .Distinct()
            .Select(n => ($"EMPAQUE x{n}", (decimal)n, (string?)null))
            .ToList();

        return Create(
            tenantId,
            variantId,
            productId,
            new ProfileChanges(UomCode.Parse("UND"), baseUomPrecision, TrackingPolicy.None, false, null, null, null, null, null, null, null, conversions),
            now);
    }

    public static VariantLogisticsProfile Create(Guid tenantId, Guid variantId, Guid productId, ProfileChanges changes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var profile = new VariantLogisticsProfile
        {
            TenantId = tenantId,
            VariantId = variantId,
            ProductId = productId,
            Version = 1,
            CreatedAt = now,
        };
        profile.Apply(changes, now);
        return profile;
    }

    public void Update(ProfileChanges changes, bool hasStock, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(changes);

        // RF-010: con saldo, cambiar el rastreo dejaría stock sin lote o serie (o con uno que ya no se pide). Pasa por un
        // documento de identificación que este corte no tiene.
        if (hasStock && changes.Tracking != Tracking)
        {
            throw new WmsDomainException(
                WmsErrorCodes.ProfileTrackingChangeRequiresIdentification,
                "The tracking policy cannot change while the variant has stock.");
        }

        // I4 (pendiente de confirmación del owner; RF-010 sólo nombra el rastreo): el saldo se guarda en la unidad base, así que con
        // saldo cambiar la unidad, bajar la precisión o cambiar el factor de una presentación que ya existía reinterpreta los saldos
        // sin avisar. Agregar o quitar una presentación y cambiar su código de barras no toca ningún saldo.
        if (hasStock && ChangesHowStockIsMeasured(changes))
        {
            throw new WmsDomainException(
                WmsErrorCodes.ProfileBaseUomChangeRequiresIdentification,
                "The base unit, a lower precision or the factor of an existing conversion cannot change while the variant has stock.");
        }

        Apply(changes, now);
        Version++;
    }

    private bool ChangesHowStockIsMeasured(ProfileChanges changes)
    {
        if (changes.BaseUom != BaseUom || changes.BaseUomPrecision < BaseUomPrecision)
        {
            return true;
        }

        // El código se compara sin distinguir mayúsculas, igual que BuildConversions.
        return changes.Conversions.Any(requested => _conversions.Any(existing =>
            string.Equals(existing.Code.Trim(), requested.Code.Trim(), StringComparison.OrdinalIgnoreCase)
            && existing.Factor != requested.Factor));
    }

    private void Apply(ProfileChanges changes, DateTimeOffset now)
    {
        if (changes.BaseUomPrecision is < UnitOfMeasure.MinPrecision or > UnitOfMeasure.MaxPrecision)
        {
            throw new WmsDomainException(WmsErrorCodes.UomPrecisionInvalid, "The precision must be between 0 and 6.");
        }

        if (changes.WeightKg < 0 || changes.VolumeM3 < 0 || changes.LengthCm < 0 || changes.WidthCm < 0 || changes.HeightCm < 0)
        {
            throw new WmsDomainException(WmsErrorCodes.ProfileMeasureInvalid, "Weight, volume and dimensions cannot be negative.");
        }

        var conversions = BuildConversions(changes.Conversions);

        BaseUom = changes.BaseUom;
        BaseUomPrecision = changes.BaseUomPrecision;
        Tracking = changes.Tracking;
        TracksExpiry = changes.TracksExpiry;
        WeightKg = changes.WeightKg;
        VolumeM3 = changes.VolumeM3;
        LengthCm = changes.LengthCm;
        WidthCm = changes.WidthCm;
        HeightCm = changes.HeightCm;
        StorageClass = string.IsNullOrWhiteSpace(changes.StorageClass) ? null : changes.StorageClass.Trim();
        DisplayUom = changes.DisplayUom;
        // En el lugar y no reconstruyendo: EF rastrea cada conversión por su Id, y una instancia nueva con el Id de una rastreada
        // se rechaza. Las que se quedan se actualizan, las nuevas se agregan y las que ya no vienen se sacan de la colección.
        _conversions.RemoveAll(existing => !conversions.Any(c => c.Existing == existing));
        foreach (var (existing, code, factor, barcode) in conversions)
        {
            if (existing is null)
            {
                _conversions.Add(UomConversion.Create(Guid.CreateVersion7(), code, factor, barcode));
            }
            else
            {
                existing.Reconfigure(code, factor, barcode);
            }
        }

        UpdatedAt = now;
    }

    /// <summary>Valida las presentaciones y conserva el id de la que mantiene su código: un identificador de barras apunta a ese
    /// id (<c>conversion_id</c>), y regenerarlo en cada <c>PUT</c> lo dejaría huérfano.</summary>
    private List<(UomConversion? Existing, string Code, decimal Factor, string? Barcode)> BuildConversions(IReadOnlyList<(string Code, decimal Factor, string? Barcode)> requested)
    {
        ArgumentNullException.ThrowIfNull(requested);

        var result = new List<(UomConversion?, string, decimal, string?)>(requested.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (rawCode, factor, barcode) in requested)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rawCode);
            // Se guarda como llegó (el sembrado «EMPAQUE x12» lleva minúscula); la unicidad se mide en mayúsculas.
            var code = rawCode.Trim();
            var key = code.ToUpperInvariant();
            ArgumentOutOfRangeException.ThrowIfGreaterThan(code.Length, UomConversion.CodeMaxLength);

            if (factor <= 0)
            {
                throw new WmsDomainException(WmsErrorCodes.ProfileConversionFactorInvalid, "The conversion factor must be greater than zero.");
            }

            if (!seen.Add(key))
            {
                throw new WmsDomainException(WmsErrorCodes.ProfileConversionDuplicate, "The conversion code is repeated.");
            }

            var existing = _conversions.FirstOrDefault(c => string.Equals(c.Code.ToUpperInvariant(), key, StringComparison.Ordinal));
            result.Add((existing, code, factor, string.IsNullOrWhiteSpace(barcode) ? null : barcode.Trim()));
        }

        return result;
    }
}
