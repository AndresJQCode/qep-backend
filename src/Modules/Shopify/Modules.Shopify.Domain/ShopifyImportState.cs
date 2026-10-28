namespace Modules.Shopify.Domain;

/// <summary>Spec 2026-10-15 §5.4: el estado de importación de una fila del espejo. Viaja con su nombre (el diccionario de textos lo
/// tiene el frontend) y se guarda como texto con su <c>CHECK</c>.</summary>
public enum ShopifyImportState
{
    /// <summary>Todavía no entró a QEP: falta una precondición (<see cref="ShopifyOrderMirror.WaitReason"/>) o aún no se evaluó.</summary>
    Waiting,

    /// <summary>Ya es un pedido de QEP. Nunca vuelve a <see cref="Waiting"/>: los cambios posteriores sólo tocan pago y cancelación.</summary>
    Imported,

    /// <summary>Se importó y después se canceló en Shopify (§6.3).</summary>
    Cancelled,

    /// <summary>QEP no lo va a traer: pedido de prueba o cancelado antes de importarse. Se guarda para que la bandeja lo muestre.</summary>
    Ignored,
}
