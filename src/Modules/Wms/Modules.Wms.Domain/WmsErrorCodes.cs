namespace Modules.Wms.Domain;

/// <summary>
/// Spec WMS 2026-10-31 §24.2 y P-6 del plan WMS-B: los códigos de Bodegas, en un solo lugar. Los 422 salen por
/// <c>WmsDomainException</c>; los 404, por <c>ResourceNotFoundException</c> en Application. El valor es el código literal que ve
/// el frontend: no se renombra.
/// </summary>
public static class WmsErrorCodes
{
    public const string WarehouseNotFound = "wms.warehouse.not_found";
    public const string WarehouseCodeTaken = "wms.warehouse.code_taken";
    public const string WarehouseCodeInvalid = "wms.warehouse.code_invalid";
    public const string WarehouseCityUnknown = "wms.warehouse.city_unknown";

    public const string LocationNotFound = "wms.location.not_found";
    public const string LocationCodeTaken = "wms.location.code_taken";
    public const string LocationCodeInvalid = "wms.location.code_invalid";
    public const string LocationParentInvalid = "wms.location.parent_invalid";
    public const string LocationKindNotAllowed = "wms.location.kind_not_allowed";
    public const string LocationRoleNotAllowed = "wms.location.role_not_allowed";

    /// <summary>S22: las cuatro virtuales (SUPPLIER, CUSTOMER, ADJUSTMENT, TRANSIT) no se editan por <c>PUT</c>.</summary>
    public const string LocationVirtualImmutable = "wms.location.virtual_immutable";
    public const string LocationHasChildren = "wms.location.has_children";
    public const string LocationHasStock = "wms.location.has_stock";
    public const string LocationCapacityInvalid = "wms.location.capacity_invalid";

    public const string OwnerNotFound = "wms.owner.not_found";
    public const string OwnerSelfImmutable = "wms.owner.self_immutable";
    public const string OwnerSelfExists = "wms.owner.self_exists";
    public const string OwnerHasStock = "wms.owner.has_stock";

    public const string UomCodeTaken = "wms.uom.code_taken";
    public const string UomCodeInvalid = "wms.uom.code_invalid";
    public const string UomUnknown = "wms.uom.unknown";
    public const string UomPrecisionInvalid = "wms.uom.precision_invalid";
    public const string UomPrecisionExceeded = "wms.uom.precision_exceeded";

    public const string ProfileVariantUnknown = "wms.profile.variant_unknown";
    public const string ProfileTrackingChangeRequiresIdentification = "wms.profile.tracking_change_requires_identification";
    public const string ProfileConversionDuplicate = "wms.profile.conversion_duplicate";
    public const string ProfileConversionFactorInvalid = "wms.profile.conversion_factor_invalid";
    public const string ProfileMeasureInvalid = "wms.profile.measure_invalid";

    public const string IdentifierValueTaken = "wms.identifier.value_taken";
    public const string IdentifierValueInvalid = "wms.identifier.value_invalid";

    public const string LabelRenderFailed = "wms.label.render_failed";
}
