using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Application;

internal static class VariantProfileDtoMapper
{
    /// <param name="stored">Falso para el default calculado en memoria (P-8): el formulario lo distingue por <c>Version = 0</c> y
    /// por las conversiones con <c>Id = Guid.Empty</c>, porque esos ids todavía no existen en la base.</param>
    public static VariantProfileDto ToDto(VariantLogisticsProfile profile, WmsVariantSnapshot variant, bool hasStock, bool stored)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(variant);

        return new VariantProfileDto(
            profile.VariantId,
            profile.ProductId,
            variant.ProductName,
            variant.Code,
            variant.VariantName,
            variant.Barcode,
            profile.BaseUom.Value,
            profile.BaseUomPrecision,
            profile.Tracking.ToString(),
            profile.TracksExpiry,
            profile.WeightKg,
            profile.VolumeM3,
            profile.LengthCm,
            profile.WidthCm,
            profile.HeightCm,
            profile.StorageClass,
            profile.DisplayUom?.Value,
            profile.Conversions
                .Select(conversion => new UomConversionDto(stored ? conversion.Id : Guid.Empty, conversion.Code, conversion.Factor, conversion.Barcode))
                .ToList(),
            hasStock,
            stored ? profile.Version : 0);
    }
}
