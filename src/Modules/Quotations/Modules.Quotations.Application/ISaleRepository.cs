using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Una venta junto a la cotización de la que salió. Viajan las dos porque el listado necesita
/// las dos: cliente, asesora, moneda y totales viven en la cotización y la venta no los repite
/// (<see cref="Sale"/> es 1:1 con ella justamente para no duplicarlos).
/// </summary>
public sealed record SaleWithQuotation(Sale Sale, Quotation Quotation);

public interface ISaleRepository
{
    Task<Sale?> FindByQuotationIdAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken);

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
