using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>Una línea del pedido para facturar. <paramref name="Subtotal"/> es el de la línea con el descuento aplicado y sin IVA.
/// <paramref name="IsGift"/> marca un regalo (subtotal e IVA en 0); <paramref name="ListUnitPrice"/> es el precio de lista con IVA
/// incluido (<c>OrderItem.UnitPrice</c>), con el que el facturador arma la base del obsequio.</summary>
public sealed record ExternalInvoicingLine(
    Guid VariantId,
    string Name,
    decimal Quantity,
    decimal Subtotal,
    decimal DiscountPercentage,
    int TaxPercentage,
    decimal TaxAmount,
    bool IsGift = false,
    decimal ListUnitPrice = 0m);

/// <summary>Una retención <b>aplicada</b> del pedido (las que no aplicaron no viajan).</summary>
public sealed record ExternalInvoicingWithholding(Guid TaxRateId, string Name, string Type, decimal Amount, decimal Percentage = 0m);

/// <summary>Un comprobante de pago: cada uno es un pago de la factura.</summary>
public sealed record ExternalInvoicingProof(Guid ProofId, decimal Amount);

/// <summary>
/// A quién se le factura, ya resuelto con la precedencia de <see cref="OrderBilledParty.SourceFor"/>: consumidor final, la parte de
/// facturación del pedido o la ficha del cliente, con lo que falte de la primera tomado de la segunda (spec 2026-11-18 §6.2). Lleva
/// datos del cliente: el <c>ToString</c> no los escribe.
/// </summary>
public sealed record ExternalInvoicingParty(
    bool IsFinalConsumer,
    PartyPersonType? PersonType,
    PartyIdentificationType? IdentificationType,
    string Identification,
    string Name,
    string? Address,
    Guid? CityId,
    Guid? DepartmentId,
    string? Country,
    string? Email,
    string? Phone)
{
    public override string ToString() => "ExternalInvoicingParty { }";
}

/// <summary>
/// La foto del pedido que un facturador externo necesita (spec 2026-11-18 §5.5). <paramref name="ProductsTotal"/> es la suma de las
/// líneas con IVA (sin envío); <paramref name="Total"/> incluye el envío; <paramref name="NetTotal"/> es <c>Total</c> menos las
/// retenciones. La cuenta de cobro viaja como banco y número: la clave con que cada integración la vincula es suya. <paramref name="Version"/>
/// es la del pedido al armar. <paramref name="AdvisorMembershipId"/> es la membresía del asesor del pedido: con ella Siigo busca el vendedor
/// vinculado (spec 2026-11-20, vendedores vinculados); <paramref name="AdvisorLabel"/> es su nombre o, sin nombre, su correo, para el texto
/// del faltante (null si ya no resuelve).
/// </summary>
public sealed record ExternalInvoicingOrder(
    Guid OrderId,
    string OrderNumber,
    string Currency,
    DateOnly Today,
    bool CashOnDelivery,
    IReadOnlyList<ExternalInvoicingLine> Lines,
    decimal FreightSubtotal,
    decimal FreightTaxAmount,
    int FreightTaxPercentage,
    decimal ProductsTotal,
    decimal Total,
    decimal RetentionAmount,
    decimal NetTotal,
    IReadOnlyList<ExternalInvoicingWithholding> Withholdings,
    IReadOnlyList<ExternalInvoicingProof> PaymentProofs,
    Guid? CompanyId,
    string? BillingAccountBankName,
    string? BillingAccountNumber,
    string? BuyerNotes,
    ExternalInvoicingParty BilledParty,
    long Version,
    Guid AdvisorMembershipId,
    string? AdvisorLabel,
    string? ShippingAddress = null,
    string? ShippingCity = null);

/// <summary>Cómo se nombra un pedido en una lista del facturador: el número y el cliente que muestra <c>/orders</c>
/// (<paramref name="CustomerName"/> es <c>null</c> si la ficha ya no resuelve).</summary>
public sealed record ExternalInvoicingOrderLabel(string OrderNumber, string? CustomerName);

/// <summary>
/// El puerto que un facturador externo (Siigo) usa sobre los pedidos (spec 2026-11-18 §5.5). El adaptador vive en Bootstrapper; este
/// puerto es de Quotations y lo implementa <see cref="ExternalOrderInvoicingService"/>.
/// </summary>
public interface IExternalOrderInvoicing
{
    /// <summary>
    /// La foto del pedido, o <c>null</c> si no es del tenant. Aplica las mismas precondiciones que la marca manual y en el mismo orden:
    /// no anulado, <b>no facturado</b> (un pedido marcado a mano no se factura de nuevo), pagado si el tenant lo exige, tipo de persona y
    /// campos del cliente facturado. Cada una es el mismo 422 de dominio de <c>InvoiceOrderHandler</c>.
    /// </summary>
    Task<ExternalInvoicingOrder?> GetForInvoicingAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);

    /// <summary>La versión actual del pedido, o <c>null</c> si no es del tenant. Sin las precondiciones de <see cref="GetForInvoicingAsync"/>:
    /// el facturador la compara con la que armó, después de crear la factura, para avisar que el pedido cambió.</summary>
    Task<long?> GetVersionAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);

    /// <summary>Si el pedido ya lleva la marca de esa factura (mismo proveedor y número, la misma regla con que
    /// <see cref="MarkInvoicedAsync"/> no hace nada). Sólo lee. <c>false</c> también si no es del tenant, si está marcado a mano o con
    /// otra factura.</summary>
    Task<bool> IsMarkedAsync(Guid tenantId, Guid orderId, string provider, string number, CancellationToken cancellationToken);

    /// <summary>El número y el nombre del cliente de estos pedidos, por id de pedido y en lote (spec 2026-11-24 §5). El nombre es el mismo
    /// <c>ClientName</c> de la lista de pedidos. Un id que no es del tenant no aparece.</summary>
    Task<IReadOnlyDictionary<Guid, ExternalInvoicingOrderLabel>> ListLabelsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken);

    /// <summary>
    /// Marca el pedido como facturado por <paramref name="provider"/> (<see cref="Order.InvoiceExternally"/>), con historial
    /// <c>Invoiced</c> y la auditoría <c>quotation.order.invoiced</c> con <c>provider</c> en <c>changedFields</c>. Idempotente: con la
    /// misma marca no hace nada. Reintenta una vez ante <c>RequestConcurrencyException</c>.
    /// </summary>
    Task MarkInvoicedAsync(
        Guid tenantId,
        Guid orderId,
        string provider,
        string number,
        string externalId,
        Guid memberId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Quita la marca de facturado de esa factura de <paramref name="provider"/> (<see cref="Order.RevertExternalInvoicing"/>), con
    /// historial <c>InvoicingReverted</c> y la auditoría <c>quotation.order.invoice_reverted</c> con <c>provider</c> y <c>number</c>.
    /// Sin marca o con otra no escribe nada. Reintenta una vez ante <c>RequestConcurrencyException</c>.
    /// </summary>
    /// <returns>Si el pedido perdió la marca.</returns>
    Task<bool> RevertExternalInvoicingAsync(
        Guid tenantId,
        Guid orderId,
        string provider,
        string number,
        Guid memberId,
        CancellationToken cancellationToken);
}
