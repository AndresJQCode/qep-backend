using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Catalog.Domain;

namespace Modules.Catalog.Infrastructure.Persistence;

/// <summary>
/// Catalog retiene a un usuario mientras alguna fila del histórico de precios lo nombre como autor
/// (<see cref="ProductPriceChange.ChangedBy"/>, <c>catalog.product_price_changes.changed_by</c>).
/// El histórico es append-only y permanente, y el reporte de cambios de precio resuelve ese id a
/// un correo (<c>ReportingPeopleLookup.EmailsByUserIdAsync</c>): borrar al usuario dejaría al
/// autor del cambio en blanco. Es historia, igual que una cotización, y no algo que se purga.
/// </summary>
/// <remarks>
/// <para>A diferencia de <c>QuotationUserReferenceProbe</c>, no pasa por membresías: la columna
/// guarda el id de <c>identity.users</c> tal cual (<c>IExecutionContext.SubjectId</c> en
/// <c>UpdateProductHandler</c> y <c>CopyPriceScalesHandler</c>). Por la misma razón no filtra por
/// tenant: un cambio en cualquier tenant es historia del usuario.</para>
/// <para>Sin índice sobre <c>changed_by</c> a propósito: el worker consulta una vez por membresía
/// quitada, y las sondas de Quotations ya recorren sin índice columnas de tablas más grandes
/// (<c>quotation_history.member_id</c>, <c>quotations.created_by</c>). Un índice lo pagaría cada
/// cambio de precio, incluida la copia masiva de escalas, para acelerar una consulta rara.</para>
/// </remarks>
internal sealed class ProductPriceChangeUserReferenceProbe(CatalogDbContext dbContext)
    : IUserReferenceProbe
{
    public string Source => "catalog";

    public Task<bool> HasReferencesAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.ProductPriceChanges.AnyAsync(
            change => change.ChangedBy == userId,
            cancellationToken);
}
