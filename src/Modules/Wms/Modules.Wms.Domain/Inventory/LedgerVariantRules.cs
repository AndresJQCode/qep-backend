namespace Modules.Wms.Domain.Inventory;

/// <summary>
/// Lo que el ledger necesita del perfil de la variante, sin conocer <c>VariantLogisticsProfile</c> (vive en <c>Warehousing</c>). Lo
/// construye el <c>InventoryLedger</c>. WMS-B guarda el código de una conversión tal como llega (<c>EMPAQUE x12</c>), así que el
/// diccionario se arma con <see cref="StringComparer.OrdinalIgnoreCase"/>: una captura en mayúsculas debe casar con la sembrada.
/// </summary>
public sealed record LedgerVariantRules(
    string BaseUom,
    int BaseUomPrecision,
    TrackingPolicy Tracking,
    IReadOnlyDictionary<string, decimal> ConversionFactors);
