using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Una venta junto a la cotización de la que salió. Viajan las dos porque el listado necesita
/// las dos: cliente, asesora, moneda y totales viven en la cotización y la venta no los repite
/// (<see cref="Sale"/> es 1:1 con ella justamente para no duplicarlos).
/// </summary>
public sealed record SaleWithQuotation(Sale Sale, Quotation Quotation);

/// <summary>
/// Un corte de un período: cuantas ventas y por cuanto, partido por estado, mas lo cargado en
/// comprobantes. Cuatro cifras que salen de una sola pasada por la base -- calcularlas del lado
/// del cliente pediria traerse todas las ventas del mes para sumarlas.
/// </summary>
public sealed record SaleWindowSummary(
    int PendingCount,
    decimal PendingTotal,
    int ApprovedCount,
    decimal ApprovedTotal,
    decimal CollectedTotal);

public interface ISaleRepository
{
    /// <summary>
    /// Los agregados de las ventas convertidas dentro de la ventana, las dos puntas inclusive.
    /// Los importes salen de la cotizacion de cada venta, que es donde viven: la venta no los
    /// repite.
    /// </summary>
    Task<SaleWindowSummary> SummarizeAsync(
        Guid tenantId,
        DateOnly convertedFrom,
        DateOnly convertedTo,
        CancellationToken cancellationToken);

    Task<Sale?> FindByQuotationIdAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken);

    /// <summary>
    /// Las ventas de varias cotizaciones de una sola vez, indexadas por id de cotizacion. Para el
    /// listado de cotizaciones, que necesita saber por fila si ya se convirtio y si esa venta
    /// sigue pendiente: con <see cref="FindByQuotationIdAsync"/> seria una consulta por fila.
    ///
    /// Sin comprobantes: el listado no los muestra.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Sale>> FindByQuotationIdsAsync(
        Guid tenantId,
        IReadOnlyCollection<QuotationId> quotationIds,
        CancellationToken cancellationToken);

    /// <summary>Por el id de la venta, para el detalle que se abre desde el listado (SALE-04).
    /// Trae los comprobantes: la pantalla los lista y ofrece descargarlos.</summary>
    Task<Sale?> FindByIdAsync(
        Guid tenantId, SaleId saleId, CancellationToken cancellationToken);

    /// <summary>
    /// Una página del listado de ventas del tenant (SALE-01), ya unida a su cotización.
    ///
    /// <paramref name="clientIds"/> es el filtro por CUC ya resuelto a ids: <c>null</c> es "sin
    /// filtro" y una colección vacía es "no matchear ninguna fila" —el CUC buscado no resolvió a
    /// ningún cliente—, mismo criterio que <c>clientIds</c> en
    /// <see cref="IQuotationRepository.SearchAsync"/>. Es independiente de
    /// <paramref name="clientId"/>, que es el filtro puntual del combobox.
    /// </summary>
    Task<(IReadOnlyList<SaleWithQuotation> Items, int Total)> SearchAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        SaleStatus? status,
        SalePaymentStatus? paymentStatus,
        DateOnly? convertedFrom,
        DateOnly? convertedTo,
        string? saleNumber,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    void Add(Sale sale);
}
