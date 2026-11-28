namespace Modules.Wms.Domain.Inventory;

/// <summary>
/// Dónde está un documento en la máquina del inventario básico (spec §5.2). Se guarda como texto: el nombre no se renombra.
/// <c>HandedToOutbound</c> lo escribirá T5; <c>ReceivedByWms</c>, T3 (§5.7). Este corte sólo los deja reservados.
/// </summary>
public enum DocumentStockStatus
{
    /// <summary>Se vio algún evento del documento pero no produjo nada (p. ej. un cancelado que llegó antes que su creado).</summary>
    None,

    /// <summary>Hay reservas de demanda abiertas.</summary>
    Reserved,

    /// <summary>Descontado al marcar el pedido como preparado.</summary>
    TakenOutByPrepare,

    /// <summary>Descontado al archivar un pedido sin preparar.</summary>
    TakenOutByArchive,

    /// <summary>La reserva se liberó (anulado) y no queda nada.</summary>
    Released,

    /// <summary>Un mensaje del documento falló más allá del umbral: espera a que pase. Sólo frena a este documento.</summary>
    Parked,

    /// <summary>T5 lo tomó para una ola: este consumidor sólo atiende la anulación.</summary>
    HandedToOutbound,

    /// <summary>Una orden de compra que ya sumó el inventario básico (recibo manual).</summary>
    Received,

    /// <summary>Una orden de compra recibida con la recepción del WMS (T3): el recibo manual no vuelve a sumar.</summary>
    ReceivedByWms,

    /// <summary>Venta del POS descontada.</summary>
    Shipped,

    /// <summary>Venta del POS anulada: lo descontado volvió.</summary>
    Voided,
}
