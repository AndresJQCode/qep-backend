namespace Modules.Siigo.Domain;

/// <summary>El estado de un pedido dentro de una tanda de facturación. Se guarda por nombre: no se reordena. <c>Pending</c> espera su
/// turno (o su próximo intento); <c>Sending</c> un worker lo tiene reclamado; <c>Invoiced</c> la factura existe en Siigo; <c>Skipped</c>
/// el pedido no estaba en condiciones y no se llamó a Siigo; <c>Failed</c> Siigo la rechazó o se agotaron los intentos.</summary>
public enum SiigoInvoiceBatchItemStatus
{
    Pending,
    Sending,
    Invoiced,
    Skipped,
    Failed,
}
