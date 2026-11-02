namespace Modules.Wms.Domain.Warehousing;

/// <summary>Los nombres viajan por la API y viven en <c>CK_locations_kind</c>: no se renombran.</summary>
public enum LocationKind
{
    Zone,
    Aisle,
    Rack,
    Level,
    Bin,
    Dock,
    Virtual,
}
