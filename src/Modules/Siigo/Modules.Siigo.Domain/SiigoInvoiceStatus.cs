namespace Modules.Siigo.Domain;

/// <summary>
/// El estado de una factura de Siigo (spec 2026-11-18 §5.6). Se guarda por nombre: no se reordena. <c>Pending</c> la petición se guardó
/// y todavía no se sabe si Siigo la creó; <c>Created</c> existe en Siigo (con la DIAN apagada la emite una persona allá); <c>Stamped</c>
/// ya tiene CUFE; <c>StampRejected</c> la DIAN la rechazó; <c>Rejected</c> Siigo no la creó y el pedido sigue libre; <c>Unknown</c> una
/// <c>Pending</c> de más de un día que sólo resuelve una persona; <c>Failed</c> nunca se creó y se descartó. <c>Rejected</c> y
/// <c>Failed</c> no cuentan como vigentes: el pedido se puede facturar de nuevo.
/// </summary>
public enum SiigoInvoiceStatus
{
    Pending,
    Created,
    Stamped,
    StampRejected,
    Rejected,
    Unknown,
    Failed,
}
