namespace Modules.Siigo.Domain;

/// <summary>El estado de un pedido dentro de una tanda de facturación. Se guarda por nombre: no se reordena y uno nuevo va al final.
/// <c>Pending</c> espera su turno; <c>Sending</c> un worker lo tiene reclamado; <c>Invoiced</c> la factura existe en Siigo; <c>Skipped</c>
/// el pedido no estaba en condiciones y no se llamó a Siigo para crear; <c>Failed</c> no se facturó y se sabe (rechazo de datos,
/// conexión, permiso o corte antes de mandar; y también un pedido cuya factura existe pero cuya de flete sigue <c>Pending</c>, haya
/// salido o no); <c>ToConfirm</c> se mandó y no se sabe si Siigo creó la del pedido: la fila de la factura sigue viva con la misma clave
/// y la tanda ya no depende del worker sino de una persona.</summary>
public enum SiigoInvoiceBatchItemStatus
{
    Pending,
    Sending,
    Invoiced,
    Skipped,
    Failed,
    ToConfirm,
}
