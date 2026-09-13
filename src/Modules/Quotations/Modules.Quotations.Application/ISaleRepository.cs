using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Una venta junto a la cotización de la que salió. Viajan las dos porque el listado necesita
/// las dos: cliente, asesora, moneda y totales viven en la cotización y la venta no los repite
/// (<see cref="Sale"/> es 1:1 con ella justamente para no duplicarlos).
/// </summary>
public sealed record SaleWithQuotation(Sale Sale, Quotation Quotation);

/// <summary>
/// Dónde quedó el export de ventas (spec 2026-09-12, D8): la fecha de conversión y el número de la
/// última venta leída, el mismo orden que el listado. El número y no el id porque
/// <see cref="SaleId"/> no se compara, y el número es único por tenant
/// (<c>IX_sales_tenant_number</c>).
/// </summary>
public sealed record SaleExportCursor(DateTimeOffset ConvertedAt, string SaleNumber);

public interface ISaleRepository
{
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

    /// <summary>Si hay al menos una venta con los filtros del listado: el paso 3 de D4 antes de
    /// encolar una exportación.</summary>
    Task<bool> AnyForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        SaleStatus? status,
        SalePaymentStatus? paymentStatus,
        DateOnly? convertedFrom,
        DateOnly? convertedTo,
        string? saleNumber,
        CancellationToken cancellationToken);

    /// <summary>Un lote de las ventas del listado, en su mismo orden, para el Excel de
    /// SalesExportProcessor (D8). Mismos filtros y semántica que <see cref="SearchAsync"/>. Keyset
    /// y no offset: <paramref name="after"/> es la clave de la última venta del lote anterior
    /// (<c>null</c> en el primero).</summary>
    Task<IReadOnlyList<SaleWithQuotation>> ListForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        SaleStatus? status,
        SalePaymentStatus? paymentStatus,
        DateOnly? convertedFrom,
        DateOnly? convertedTo,
        string? saleNumber,
        SaleExportCursor? after,
        int limit,
        CancellationToken cancellationToken);

    void Add(Sale sale);
}
