namespace Modules.Siigo.Domain;

/// <summary>Qué factura es: la del pedido, o la de flete cuando la conexión factura el flete aparte (spec 2026-11-18 §5.6). Se guarda
/// por nombre: no se reordena.</summary>
public enum SiigoInvoiceKind
{
    Order,
    Freight,
}
