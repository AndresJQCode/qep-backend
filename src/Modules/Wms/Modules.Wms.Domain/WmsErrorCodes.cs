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

    // Ledger y saldos (plan WMS-D). UomPrecisionExceeded ya viene de WMS-B.
    public const string StockInsufficient = "wms.stock.insufficient";
    public const string StockReserved = "wms.stock.reserved";
    public const string StockStatusInvalid = "wms.stock.status_invalid";
    public const string IdempotencyKeyRequired = "wms.idempotency_key_required";
    public const string IdempotencyKeyReused = "wms.idempotency_key_reused";
    public const string LedgerImmutable = "wms.ledger.immutable";

    public const string LotRequired = "wms.lot.required";
    public const string LotNotAllowed = "wms.lot.not_allowed";
    public const string LotExpiryRequired = "wms.lot.expiry_required";
    public const string LotCodeInvalid = "wms.lot.code_invalid";
    public const string LotNotFound = "wms.lot.not_found";
    public const string SerialRequired = "wms.serial.required";
    public const string UomFactorMismatch = "wms.uom.factor_mismatch";

    public const string LocationBlocked = "wms.location.blocked";

    /// <summary>S20: 422 por <c>WmsDomainException</c>, no 409.</summary>
    public const string LocationCounting = "wms.location.counting";

    public const string MovementAlreadyReversed = "wms.movement.already_reversed";
    public const string MovementNotFound = "wms.movement.not_found";
    public const string MovementQuantityInvalid = "wms.movement.quantity_invalid";
    public const string MovementSameKey = "wms.movement.same_key";

    /// <summary>S12, nuevo en §24.2: la fecha física del movimiento fuera de la ventana permitida.</summary>
    public const string MovementOccurredAtOutOfRange = "wms.movement.occurred_at_out_of_range";
    public const string BatchTooLarge = "wms.batch.too_large";

    public const string DocumentInvalidTransition = "wms.document.invalid_transition";
    public const string DocumentBusy = "wms.document.busy";
    public const string AdjustmentNotFound = "wms.adjustment.not_found";
    public const string AdjustmentLineInvalid = "wms.adjustment.line_invalid";

    public const string ImportAmbiguousCode = "wms.import.ambiguous_code";
    public const string ImportUnknownCode = "wms.import.unknown_code";
    public const string ImportUnknownLocation = "wms.import.unknown_location";
    public const string ImportDuplicateRow = "wms.import.duplicate_row";
    public const string ImportInvalidFile = "wms.import.invalid_file";

    /// <summary>S21, nuevo en §24.2.</summary>
    public const string ImportTooManyRows = "wms.import.too_many_rows";
    public const string ImportPreviewNotFound = "wms.import.preview_not_found";
    public const string ImportPreviewNotCommittable = "wms.import.preview_not_committable";
    public const string ImportPreviewExpired = "wms.import.preview_expired";
    public const string ExportEmpty = "wms.export.empty";
    public const string ExportTooManyRows = "wms.export.too_many_rows";
}
