namespace Modules.Wms.Domain.Warehousing;

/// <summary>Límites opcionales de una ubicación; <c>null</c> es «sin límite». Los negativos los rechaza <c>Warehouse</c>.</summary>
public readonly record struct LocationCapacity(decimal? MaxWeightKg, decimal? MaxVolumeM3, int? MaxUnits)
{
    public static LocationCapacity None => new(null, null, null);
}
