using BuildingBlocks.Domain.Catalog;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>Una línea física del pedido, para el contenido de la guía.</summary>
public sealed record ExternalShippingLine(string Name, string? Code, decimal Quantity);

/// <summary>
/// A quién se le entrega (spec 2026-11-21 §5.7): la parte de envío del pedido o, sin ella, la ficha del cliente, con el mismo criterio
/// que <c>ExternalOrderInvoicingService</c> para el despacho. <paramref name="Country"/> <c>null</c> es Colombia; fuera de Colombia la
/// ciudad viene como texto en <paramref name="CityName"/> y no hay <paramref name="CityId"/>. <paramref name="Identification"/> es la de la
/// parte de facturación o del cliente, si la hay. Lleva datos del destinatario: el <c>ToString</c> no los escribe.
/// </summary>
public sealed record ExternalShippingRecipient(
    string? Name,
    string? Phone,
    string? Address,
    Guid? CityId,
    string? Country,
    string? CityName,
    string? Identification)
{
    public override string ToString() => "ExternalShippingRecipient { }";
}

/// <summary>
/// La foto del pedido que una transportadora externa (Coordinadora) necesita para la guía (spec 2026-11-21 §5.7). Sólo lectura: el
/// pedido no cambia por tener guía. <paramref name="ProductsTotal"/> es la suma de las líneas con IVA (sin envío): el valor declarado;
/// <paramref name="NetTotal"/> lo que se recauda contraentrega. <paramref name="CarrierName"/> es la foto de la transportadora elegida en
/// el pedido, o <c>null</c>. <paramref name="Today"/> es hoy en la zona del tenant.
/// </summary>
public sealed record ExternalShippingOrder(
    Guid OrderId,
    string OrderNumber,
    DateOnly Today,
    bool IsCancelled,
    bool IsStorePickup,
    bool RequiresShipping,
    string? CarrierName,
    bool CashOnDelivery,
    decimal ProductsTotal,
    decimal NetTotal,
    string? Notes,
    long Version,
    IReadOnlyList<ExternalShippingLine> Lines,
    ExternalShippingRecipient Recipient)
{
    public override string ToString() => $"ExternalShippingOrder {{ OrderId = {OrderId}, OrderNumber = {OrderNumber} }}";
}

/// <summary>El puerto que una transportadora externa usa sobre los pedidos (spec 2026-11-21 §5.7). El adaptador vive en Bootstrapper; este
/// puerto es de Quotations y lo implementa <see cref="ExternalOrderShippingService"/>.</summary>
public interface IExternalOrderShipping
{
    /// <summary>La foto del pedido, o <c>null</c> si no es del tenant. Sin precondiciones: las decide quien arma la guía, que las junta todas.</summary>
    Task<ExternalShippingOrder?> GetForShippingAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);
}

/// <summary>
/// Spec 2026-11-21 §5.7: el pedido visto por una transportadora externa. El destinatario sale de la parte de envío del pedido y, cuando no
/// hay («los mismos del cliente»), de la ficha del cliente; la identificación, de la parte de facturación o de la ficha. Sólo lectura.
/// </summary>
public sealed class ExternalOrderShippingService(
    IOrderRepository orders,
    ITenantClock tenantClock,
    IQuotationCustomerLookup customerLookup) : IExternalOrderShipping
{
    public async Task<ExternalShippingOrder?> GetForShippingAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken)
    {
        var order = await orders.FindUntrackedByIdAsync(tenantId, new OrderId(orderId), cancellationToken);
        if (order is null)
        {
            return null;
        }

        var calendar = await tenantClock.GetAsync(tenantId, cancellationToken);
        var customer = await customerLookup.FindAsync(tenantId, order.ClientId, cancellationToken);
        var recipient = Recipient(order, customer);
        var items = order.Items.OrderBy(item => item.Position).ToList();

        return new ExternalShippingOrder(
            order.Id.Value,
            order.OrderNumber,
            calendar.Today,
            order.Status == OrderStatus.Cancelled,
            order.IsStorePickup,
            order.RequiresShipping,
            order.CarrierName,
            order.CashOnDelivery,
            items.Sum(item => item.Subtotal + item.TaxAmount),
            order.NetTotal,
            order.Notes,
            order.Version,
            [.. items
                .Where(item => item.Kind == ProductKind.Physical)
                .Select(item => new ExternalShippingLine(item.ProductName ?? item.ProductCode ?? string.Empty, item.ProductCode, item.Quantity))],
            recipient);
    }

    // La parte de envío manda; lo que falte lo da la ficha del cliente. Sin parte de envío, el despacho va a la ficha, como la factura.
    private static ExternalShippingRecipient Recipient(Order order, QuotationCustomerRef? customer)
    {
        var shipping = order.Shipping;
        var identification = FirstNonBlank(order.Billing?.IdentificationNumber, customer?.IdentificationNumber);
        if (shipping is not null)
        {
            return new ExternalShippingRecipient(
                FirstNonBlank(shipping.Name, customer?.Name),
                FirstNonBlank(shipping.Phone, customer?.Phone),
                shipping.Address,
                shipping.CityId,
                shipping.Country,
                shipping.CityName,
                identification);
        }

        return new ExternalShippingRecipient(
            customer?.Name,
            customer?.Phone,
            customer?.Address,
            customer?.CityId,
            customer?.Country,
            customer?.CityName,
            identification);
    }

    private static string? FirstNonBlank(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first : string.IsNullOrWhiteSpace(second) ? null : second;
}
