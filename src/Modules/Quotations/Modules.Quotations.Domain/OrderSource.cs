namespace Modules.Quotations.Domain;

/// <summary>De dónde nació el pedido (spec 2026-10-15 §4.1). Cada integración agrega su valor y el
/// <c>CHECK</c> de la tabla se amplía con él.</summary>
public enum OrderSource
{
    Quotation,
    Manual,
    Shopify
}
