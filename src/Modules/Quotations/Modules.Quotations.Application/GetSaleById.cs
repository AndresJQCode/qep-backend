using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// SALE-04: el detalle de una venta, direccionada por su propio id.
///
/// Existe aparte de <see cref="GetSaleQuery"/> —que entra por la cotización— porque el listado
/// muestra ventas: desde una fila no hay por dónde entrar si la única ruta cuelga de otro
/// recurso.
/// </summary>
public sealed record GetSaleByIdQuery(Guid TenantId, Guid SaleId) : IQuery<SaleDetailDto>;

/// <summary>
/// La venta junto a su cotización. Viajan las dos porque la pantalla necesita las dos: cliente,
/// partes, productos y totales viven en la cotización y la venta no los repite.
///
/// Una sola respuesta y no dos llamadas encadenadas: pedir primero la venta para enterarse del
/// `quotationId` y recién entonces la cotización es una cascada que el backend puede evitar, y
/// este módulo ya tiene las dos a mano.
/// </summary>
public sealed record SaleDetailDto(SaleDto Sale, QuotationDto Quotation);

public sealed class GetSaleByIdHandler(
    ISaleRepository saleRepository,
    IQuotationRepository quotationRepository,
    IExecutionContext executionContext)
    : IQueryHandler<GetSaleByIdQuery, SaleDetailDto>
{
    public async Task<SaleDetailDto> HandleAsync(
        GetSaleByIdQuery query,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, SalesPermissions.SaleRead);

        var sale = await saleRepository.FindByIdAsync(
            query.TenantId, new SaleId(query.SaleId), cancellationToken)
            ?? throw SaleNotFound.ById(query.SaleId);

        // Sin la cotización no hay detalle que dibujar: la venta no guarda ni el cliente ni las
        // líneas. Que falte seria una venta huérfana --imposible por la FK-- asi que se trata
        // como "no encontrada" y no como un detalle a medias.
        var quotation = await quotationRepository.FindAsync(
            query.TenantId, sale.QuotationId, cancellationToken)
            ?? throw SaleNotFound.ById(query.SaleId);

        return new SaleDetailDto(sale.ToDto(), quotation.ToDto());
    }
}
