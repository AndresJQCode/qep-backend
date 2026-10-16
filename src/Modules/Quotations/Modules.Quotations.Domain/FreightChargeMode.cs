namespace Modules.Quotations.Domain;

/// <summary>Quién paga el envío y cómo entra en el documento (spec 2026-10-17 flete).</summary>
public enum FreightChargeMode
{
    /// <summary>El envío se cobra en el documento: suma su subtotal y su IVA al total.</summary>
    Charged,

    /// <summary>El cliente paga al recibir: el documento no suma nada.</summary>
    OnDelivery,

    /// <summary>Envío sin costo para el cliente.</summary>
    Free,
}
