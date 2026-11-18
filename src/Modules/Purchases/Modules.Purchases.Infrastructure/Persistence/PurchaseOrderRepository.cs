using Microsoft.EntityFrameworkCore;
using Modules.Purchases.Application;
using Modules.Purchases.Domain;

namespace Modules.Purchases.Infrastructure.Persistence;

internal sealed class PurchaseOrderRepository(PurchasesDbContext dbContext) : IPurchaseOrderRepository
{
    public Task<PurchaseOrder?> GetAsync(Guid tenantId, PurchaseOrderId id, CancellationToken cancellationToken) =>
        WithOrderedLines(dbContext.PurchaseOrders)
            .FirstOrDefaultAsync(order => order.TenantId == tenantId && order.Id == id, cancellationToken);

    public void Add(PurchaseOrder order) => dbContext.PurchaseOrders.Add(order);

    public void Remove(PurchaseOrder order) => dbContext.PurchaseOrders.Remove(order);

    public async Task<(IReadOnlyList<PurchaseOrder> Items, int Total)> SearchAsync(
        Guid tenantId,
        PurchaseOrderStatus? status,
        Guid? supplierId,
        string? numberSearch,
        IReadOnlyCollection<Guid>? supplierIdsMatchingSearch,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var orders = FilteredQuery(tenantId, supplierId, numberSearch, supplierIdsMatchingSearch);
        if (status is { } wanted)
        {
            orders = orders.Where(order => order.Status == wanted);
        }

        var total = await orders.CountAsync(cancellationToken);
        // Las líneas viajan con la página: el resumen cuenta líneas y suma el total sin otra consulta por orden.
        var items = await WithOrderedLines(orders)
            .OrderByDescending(order => order.CreatedAt)
            .ThenByDescending(order => order.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<IReadOnlyDictionary<PurchaseOrderStatus, int>> CountByStatusAsync(
        Guid tenantId,
        Guid? supplierId,
        string? numberSearch,
        IReadOnlyCollection<Guid>? supplierIdsMatchingSearch,
        CancellationToken cancellationToken)
    {
        // Una sola ida con GROUP BY (D-18); un estado sin filas no viene y el handler lo completa en 0.
        var rows = await FilteredQuery(tenantId, supplierId, numberSearch, supplierIdsMatchingSearch)
            .GroupBy(order => order.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(row => row.Status, row => row.Count);
    }

    private IQueryable<PurchaseOrder> FilteredQuery(
        Guid tenantId,
        Guid? supplierId,
        string? numberSearch,
        IReadOnlyCollection<Guid>? supplierIdsMatchingSearch)
    {
        var orders = dbContext.PurchaseOrders.AsNoTracking().Where(order => order.TenantId == tenantId);
        if (supplierId is { } supplier)
        {
            orders = orders.Where(order => order.SupplierId == supplier);
        }

        // D-11: el número (ILIKE escapado) o un proveedor cuyo nombre coincide, cuyos ids ya resolvió el directorio.
        var pattern = LikeSearch.ContainsPattern(numberSearch);
        if (pattern is not null)
        {
            var supplierIds = supplierIdsMatchingSearch is { Count: > 0 } ids ? ids.ToArray() : [];
            orders = orders.Where(order =>
                EF.Functions.ILike(order.Number, pattern, LikeSearch.EscapeCharacter)
                || supplierIds.Contains(order.SupplierId));
        }

        return orders;
    }

    // El historial es owned y viaja solo; las líneas, en el orden del cuerpo.
    private static IQueryable<PurchaseOrder> WithOrderedLines(IQueryable<PurchaseOrder> orders) =>
        orders.Include(order => order.Lines.OrderBy(line => EF.Property<int>(line, PurchasesDbContext.LineOrdinal)));
}
