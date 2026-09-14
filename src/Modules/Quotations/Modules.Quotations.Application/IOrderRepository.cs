using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Un pedido junto a la cotización de la que salió. Viajan las dos porque el listado necesita
/// las dos: cliente, asesora, moneda y totales viven en la cotización y el pedido no los repite
/// (<see cref="Order"/> es 1:1 con ella justamente para no duplicarlos).
/// </summary>
public sealed record OrderWithQuotation(Order Order, Quotation Quotation);

/// <summary>
/// Dónde quedó el export de pedidos (spec 2026-09-12, D8): la fecha de conversión y el número del
/// último pedido leído, el mismo orden que el listado. El número y no el id porque
/// <see cref="OrderId"/> no se compara, y el número es único por tenant
/// (<c>IX_sales_tenant_number</c>).
/// </summary>
public sealed record OrderExportCursor(DateTimeOffset ConvertedAt, string OrderNumber);

public interface IOrderRepository
{
    Task<Order?> FindByQuotationIdAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken);

    /// <summary>
    /// Los pedidos de varias cotizaciones de una sola vez, indexados por id de cotizacion. Para el
    /// listado de cotizaciones, que necesita saber por fila si ya se convirtio y si ese pedido
    /// sigue pendiente: con <see cref="FindByQuotationIdAsync"/> seria una consulta por fila.
    ///
    /// Sin comprobantes: el listado no los muestra.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Order>> FindByQuotationIdsAsync(
        Guid tenantId,
        IReadOnlyCollection<QuotationId> quotationIds,
        CancellationToken cancellationToken);

    /// <summary>Por el id del pedido, para el detalle que se abre desde el listado (SALE-04).
    /// Trae los comprobantes: la pantalla los lista y ofrece descargarlos.</summary>
    Task<Order?> FindByIdAsync(
        Guid tenantId, OrderId orderId, CancellationToken cancellationToken);

    /// <summary>
    /// Una página del listado de pedidos del tenant (SALE-01), ya unida a su cotización.
    ///
    /// <paramref name="clientIds"/> es el filtro por CUC ya resuelto a ids: <c>null</c> es "sin
    /// filtro" y una colección vacía es "no matchear ninguna fila" —el CUC buscado no resolvió a
    /// ningún cliente—, mismo criterio que <c>clientIds</c> en
    /// <see cref="IQuotationRepository.SearchAsync"/>. Es independiente de
    /// <paramref name="clientId"/>, que es el filtro puntual del combobox.
    /// </summary>
    Task<(IReadOnlyList<OrderWithQuotation> Items, int Total)> SearchAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        OrderStatus? status,
        OrderPaymentStatus? paymentStatus,
        DateOnly? convertedFrom,
        DateOnly? convertedTo,
        string? orderNumber,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Si hay al menos un pedido con los filtros del listado: el paso 3 de D4 antes de
    /// encolar una exportación.</summary>
    Task<bool> AnyForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        OrderStatus? status,
        OrderPaymentStatus? paymentStatus,
        DateOnly? convertedFrom,
        DateOnly? convertedTo,
        string? orderNumber,
        CancellationToken cancellationToken);

    /// <summary>Un lote de los pedidos del listado, en su mismo orden, para el Excel de
    /// OrdersExportProcessor (D8). Mismos filtros y semántica que <see cref="SearchAsync"/>. Keyset
    /// y no offset: <paramref name="after"/> es la clave del último pedido del lote anterior
    /// (<c>null</c> en el primero).</summary>
    Task<IReadOnlyList<OrderWithQuotation>> ListForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        OrderStatus? status,
        OrderPaymentStatus? paymentStatus,
        DateOnly? convertedFrom,
        DateOnly? convertedTo,
        string? orderNumber,
        OrderExportCursor? after,
        int limit,
        CancellationToken cancellationToken);

    void Add(Order order);
}
