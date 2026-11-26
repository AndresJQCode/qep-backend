using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.Domain.Warehousing;

/// <summary>Los campos editables del perfil logístico de una variante, tal como llegan del <c>PUT</c>.</summary>
public sealed record ProfileChanges(
    UomCode BaseUom,
    int BaseUomPrecision,
    TrackingPolicy Tracking,
    bool TracksExpiry,
    decimal? WeightKg,
    decimal? VolumeM3,
    decimal? LengthCm,
    decimal? WidthCm,
    decimal? HeightCm,
    string? StorageClass,
    UomCode? DisplayUom,
    IReadOnlyList<(string Code, decimal Factor, string? Barcode)> Conversions);
