namespace Modules.Purchases.Domain;

/// <summary>Cómo terminó el último intento de mandar la orden por correo al proveedor. Se guarda como texto.</summary>
public enum PurchaseOrderEmailStatus
{
    Sent,
    Failed,

    /// <summary>El proveedor no tiene correo: no se llamó al canal.</summary>
    NoRecipient,
}
