namespace Modules.Quotations.Domain;

/// <summary>
/// A quién describe una <see cref="QuotationParty"/>: a quién se le factura o a quién se le
/// entrega. Dos valores hoy; el enum existe para que un tercero mañana (retiro en tienda, un
/// tercero pagador) sea un valor más y no una tanda de columnas nuevas.
/// </summary>
public enum QuotationPartyRole
{
    Billing = 1,
    Shipping = 2
}
