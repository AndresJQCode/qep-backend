namespace Modules.Wms.Domain.Inventory;

/// <summary>Por qué se movió el stock. Se persiste como texto: el nombre es contrato con la base y con los reportes.</summary>
public enum MovementReason
{
    Receipt,
    Putaway,
    InternalMove,
    TransferDispatch,
    TransferReceipt,
    Pick,
    Ship,
    Adjustment,
    InitialLoad,
    Reversal,
    StatusChange,
    ReturnToSupplier,
    WriteOff,
}
