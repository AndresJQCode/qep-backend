namespace Modules.Wms.Domain.Warehousing;

/// <summary>Sin <c>Location</c>: el código de una ubicación es único por bodega y la app lo resuelve contra <c>scannable</c>
/// (S7, P-1). <c>CK_identifiers_kind</c> lista estos mismos cuatro.</summary>
public enum IdentifierKind
{
    Variant,
    Presentation,
    Lot,
    LogisticUnit,
}
