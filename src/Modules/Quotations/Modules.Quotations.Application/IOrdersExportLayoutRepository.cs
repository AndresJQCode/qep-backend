using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// El layout de columnas del Excel de pedidos, uno por tenant (spec 2026-09-24, D6). Sin
/// <c>Update</c>: <see cref="FindAsync"/> devuelve la entidad rastreada y
/// <see cref="IQuotationsUnitOfWork.SaveChangesAsync"/> persiste el <c>Replace</c>, como en el
/// resto de los repositorios del módulo. Sin fila no hay layout: el llamador arma el por defecto
/// con <see cref="OrdersExportLayout.CreateDefault"/> (D9) y lo agrega si algo cambió.
/// </summary>
public interface IOrdersExportLayoutRepository
{
    Task<OrdersExportLayout?> FindAsync(Guid tenantId, CancellationToken cancellationToken);

    void Add(OrdersExportLayout layout);
}
