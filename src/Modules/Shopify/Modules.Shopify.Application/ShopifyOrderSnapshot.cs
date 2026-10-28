using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-10-15 §5.2, §5.3 y §6.2: el pedido de Shopify ya normalizado, igual venga del webhook (REST) o de la conciliación
/// (GraphQL). Los montos están en la moneda de la tienda (<c>shop_money</c>/<c>shopMoney</c>), no en la del comprador. Los
/// enums de Shopify viajan en minúsculas con guion bajo, como los da REST (<c>paid</c>, <c>partially_paid</c>, <c>customer</c>).
/// <see cref="FinancialStatus"/> vacío significa que Shopify no lo mandó.
/// <para>El parser sólo garantiza forma: <see cref="OrderId"/>, <see cref="Name"/>, <see cref="CreatedAt"/>,
/// <see cref="UpdatedAt"/>, <see cref="Currency"/> y <see cref="TotalPrice"/> presentes y bien tipados. Los anchos (nombre,
/// estado, moneda) los valida el dominio al recibir <see cref="ToHeader"/>; el parser no recorta nada.</para>
/// </summary>
public sealed record ShopifyOrderSnapshot(
    long OrderId,
    string Name,
    long? CustomerId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    string FinancialStatus,
    string Currency,
    decimal TotalPrice,
    bool TaxesIncluded,
    bool Test,
    string? Note,
    IReadOnlyList<string> PaymentGateways,
    ShopifyBuyer Buyer,
    ShopifyAddress? ShippingAddress,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<ShopifyLine> Lines,
    IReadOnlyList<ShopifyShippingLine> ShippingLines)
{
    /// <summary>El único lugar donde el snapshot se vuelve la cabecera del espejo (§5.4). No valida: si un valor no cabe,
    /// <c>ShopifyOrderMirror.Create</c>/<c>ApplySnapshot</c> lanza <see cref="ArgumentException"/>, que es el fallo
    /// permanente.</summary>
    public ShopifyOrderHeader ToHeader() => new(
        Name,
        CustomerId,
        CreatedAt,
        CancelledAt,
        FinancialStatus.Length == 0 ? null : FinancialStatus,
        Currency,
        TotalPrice,
        TaxesIncluded);
}

/// <summary>Una línea del pedido. <see cref="Sku"/> nulo cuando Shopify no lo trae o viene vacío (el importador responde
/// <c>product_unmatched</c>); <see cref="DiscountAmount"/> es la suma de las asignaciones de descuento de la línea, sobre
/// <c>OriginalUnitPrice × Quantity</c>.</summary>
public sealed record ShopifyLine(string? Sku, decimal Quantity, decimal OriginalUnitPrice, decimal DiscountAmount);

/// <summary>Una línea de envío, retiradas incluidas: descartarlas es del importador (§6.2.2). <see cref="TaxAmount"/> es la suma
/// de sus <c>tax_lines</c>/<c>taxLines</c>.</summary>
public sealed record ShopifyShippingLine(string? Code, string? Title, decimal DiscountedPrice, decimal TaxAmount, bool IsRemoved);

/// <summary>El comprador: nombre del cliente de Shopify y email/teléfono del pedido, o los del cliente si el pedido no los trae.
/// Todo nulo en un pedido sin cliente.</summary>
public sealed record ShopifyBuyer(string? FirstName, string? LastName, string? Email, string? Phone);

/// <summary>La dirección de envío. <see cref="ProvinceCode"/> es ISO 3166-2 sin el prefijo del país (V-4). <see cref="Company"/> es el
/// campo «Empresa» del checkout, que muchas tiendas reusan para pedir el documento (<see cref="ShopifyDocumentSources"/>).</summary>
public sealed record ShopifyAddress(
    string? Name,
    string? Address1,
    string? Address2,
    string? City,
    string? Province,
    string? ProvinceCode,
    string? CountryCode,
    string? Phone,
    string? Company = null);
