using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Infrastructure.Persistence;

internal sealed class OrderRepository(QuotationsDbContext dbContext) : IOrderRepository
{
    private const string LikeEscapeCharacter = "\\";

    // Mismo criterio que QuotationRepository: un numero de pedido buscado que por casualidad trae
    // "%" o "_" no debe actuar como comodin SQL.
    private static string EscapeLikeWildcards(string term) => term
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static string? LikePattern(string? term)
    {
        var trimmed = term?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : $"%{EscapeLikeWildcards(trimmed)}%";
    }

    public Task<Order?> FindByQuotationIdAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken) =>
        dbContext.Orders
            .Include(order => order.PaymentProofs)
            .SingleOrDefaultAsync(
                order => order.TenantId == tenantId && order.QuotationId == quotationId,
                cancellationToken);

    // AsNoTracking y sin comprobantes: esto alimenta una fila de listado, que solo pregunta si
    // la cotizacion ya se convirtio y como quedo ese pedido. La relacion es 1:1, asi que indexar
    // por cotizacion no puede perder filas.
    public async Task<IReadOnlyDictionary<Guid, Order>> FindByQuotationIdsAsync(
        Guid tenantId,
        IReadOnlyCollection<QuotationId> quotationIds,
        CancellationToken cancellationToken)
    {
        if (quotationIds.Count == 0)
        {
            return new Dictionary<Guid, Order>();
        }

        var ids = quotationIds.ToArray();
        var orders = await dbContext.Orders
            .AsNoTracking()
            .Where(order => order.TenantId == tenantId && ids.Contains(order.QuotationId))
            .ToListAsync(cancellationToken);

        return orders.ToDictionary(order => order.QuotationId.Value);
    }

    public Task<Order?> FindByIdAsync(
        Guid tenantId, OrderId orderId, CancellationToken cancellationToken) =>
        dbContext.Orders
            .Include(order => order.PaymentProofs)
            .SingleOrDefaultAsync(
                order => order.TenantId == tenantId && order.Id == orderId,
                cancellationToken);

    public async Task<(IReadOnlyList<OrderWithQuotation> Items, int Total)> SearchAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        OrderStatus? status,
        OrderPaymentStatus? paymentStatus,
        DateTimeOffset? convertedFrom,
        DateTimeOffset? convertedBefore,
        string? orderNumber,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        // El join va acá adentro y no en el composition root --como sí lo hace el reporte de
        // pedidos-- porque las dos tablas son de este módulo y viven en el mismo DbContext.
        var (orders, quotations) = Filtered(
            tenantId, clientId, clientIds, advisorId, status, paymentStatus, convertedFrom, convertedBefore, orderNumber);
        var joined =
            from order in orders
            join quotation in quotations on order.QuotationId equals quotation.Id
            select new { order, quotation };

        var total = await joined.CountAsync(cancellationToken);
        var items = await joined
            // Por fecha de conversión, y el número como desempate: dos pedidos del mismo instante
            // --el mismo segundo en una siembra de prueba-- tienen que paginar en un orden
            // estable, o una fila puede repetirse o saltarse entre páginas.
            .OrderByDescending(row => row.order.ConvertedAt)
            .ThenByDescending(row => row.order.OrderNumber)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(row => new OrderWithQuotation(row.order, row.quotation))
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public Task<bool> AnyForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        OrderStatus? status,
        OrderPaymentStatus? paymentStatus,
        DateTimeOffset? convertedFrom,
        DateTimeOffset? convertedBefore,
        string? orderNumber,
        CancellationToken cancellationToken)
    {
        var (orders, quotations) = Filtered(
            tenantId, clientId, clientIds, advisorId, status, paymentStatus, convertedFrom, convertedBefore, orderNumber);
        return (from order in orders
                join quotation in quotations on order.QuotationId equals quotation.Id
                select order.Id)
            .AnyAsync(cancellationToken);
    }

    // Keyset sobre el orden exacto del listado —(ConvertedAt, OrderNumber), los dos descendentes—:
    // el lote siguiente es "lo que viene después del último pedido leído" y no un offset, así que
    // un pedido convertido o que sale del filtro durante el export no repite ni salta filas
    // (spec 2026-09-12, D8). El corte va sobre `orders`, antes del join, igual que los filtros, y es
    // una comparación de filas de Postgres, `(converted_at, order_number) < (@fecha, @numero)`, que
    // Npgsql traduce desde EF.Functions.LessThan: IX_orders_tenant_converted_at_number la resuelve
    // como un rango, y el número se compara con la collation de la columna, la misma del ORDER BY.
    public async Task<IReadOnlyList<OrderWithQuotation>> ListForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        OrderStatus? status,
        OrderPaymentStatus? paymentStatus,
        DateTimeOffset? convertedFrom,
        DateTimeOffset? convertedBefore,
        string? orderNumber,
        OrderExportCursor? after,
        int limit,
        CancellationToken cancellationToken)
    {
        var (orders, quotations) = Filtered(
            tenantId, clientId, clientIds, advisorId, status, paymentStatus, convertedFrom, convertedBefore, orderNumber);

        if (after is not null)
        {
            var convertedAt = after.ConvertedAt;
            var number = after.OrderNumber;
            orders = orders.Where(order => EF.Functions.LessThan(
                ValueTuple.Create(order.ConvertedAt, order.OrderNumber),
                ValueTuple.Create(convertedAt, number)));
        }

        return await (
                from order in orders
                join quotation in quotations on order.QuotationId equals quotation.Id
                select new { order, quotation })
            .OrderByDescending(row => row.order.ConvertedAt)
            .ThenByDescending(row => row.order.OrderNumber)
            .Take(limit)
            .Select(row => new OrderWithQuotation(row.order, row.quotation))
            .ToListAsync(cancellationToken);
    }

    // Los filtros del listado y de su Excel salen de acá y de ningún otro lado. Se aplican sobre
    // cada tabla por separado y el join se arma recién al final, proyectando ahí mismo: filtrar
    // después de proyectar a un tipo propio es lo que hacía que el proveedor no tradujera la
    // consulta y el endpoint respondiera 500. Sin comprobantes ni líneas: la fila no los muestra, y
    // traerlos sería el mismo N+1 que QuotationRepository.SearchAsync evita.
    private (IQueryable<Order> Orders, IQueryable<Quotation> Quotations) Filtered(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        OrderStatus? status,
        OrderPaymentStatus? paymentStatus,
        DateTimeOffset? convertedFrom,
        DateTimeOffset? convertedBefore,
        string? orderNumber)
    {
        var orders = dbContext.Orders
            .AsNoTracking()
            .Where(order => order.TenantId == tenantId);

        if (status is { } orderStatus)
        {
            orders = orders.Where(order => order.Status == orderStatus);
        }

        if (paymentStatus is { } orderPaymentStatus)
        {
            orders = orders.Where(order => order.PaymentStatus == orderPaymentStatus);
        }

        var orderNumberPattern = LikePattern(orderNumber);
        if (orderNumberPattern is not null)
        {
            orders = orders.Where(order =>
                EF.Functions.ILike(order.OrderNumber, orderNumberPattern, LikeEscapeCharacter));
        }

        if (convertedFrom is { } from)
        {
            orders = orders.Where(order => order.ConvertedAt >= from);
        }

        if (convertedBefore is { } before)
        {
            // Exclusivo, igual que el listado de cotizaciones: quien llama ya lo corrió al 00:00
            // local del día siguiente al "hasta" (spec 2026-09-17, punto 3).
            orders = orders.Where(order => order.ConvertedAt < before);
        }

        // Cliente y asesora viven en la cotizacion: sus filtros se aplican de ese lado.
        var quotations = dbContext.Quotations
            .AsNoTracking()
            .Where(quotation => quotation.TenantId == tenantId);

        if (clientId is { } client)
        {
            quotations = quotations.Where(quotation => quotation.ClientId == client);
        }

        if (clientIds is not null)
        {
            quotations = quotations.Where(quotation => clientIds.Contains(quotation.ClientId));
        }

        if (advisorId is { } advisor)
        {
            quotations = quotations.Where(quotation => quotation.AdvisorId == advisor);
        }

        return (orders, quotations);
    }

    // Una consulta por lote del Excel (spec 2026-09-15, E6), no una por pedido. La tabla de
    // comprobantes no tiene tenant: el filtro va por el join con `orders`, que sí lo tiene. El orden
    // —fecha de subida, y el id como desempate entre los que llegaron en el mismo request— es el de
    // las columnas del Excel. Se agrupa en memoria, donde GroupBy conserva ese orden.
    public async Task<IReadOnlyDictionary<OrderId, IReadOnlyList<OrderExportPaymentProof>>> ListPaymentProofsForExportAsync(
        Guid tenantId,
        IReadOnlyCollection<OrderId> orderIds,
        CancellationToken cancellationToken)
    {
        if (orderIds.Count == 0)
        {
            return new Dictionary<OrderId, IReadOnlyList<OrderExportPaymentProof>>();
        }

        var ids = orderIds.ToArray();
        var rows = await (
                from proof in dbContext.OrderPaymentProofs.AsNoTracking()
                join order in dbContext.Orders.AsNoTracking() on proof.OrderId equals order.Id
                where order.TenantId == tenantId && ids.Contains(order.Id)
                orderby proof.UploadedAt, proof.Id
                select new { proof.OrderId, proof.Id, proof.PublicStorageKey, proof.UploadedAt })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.OrderId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<OrderExportPaymentProof>)group
                    .Select(row => new OrderExportPaymentProof(row.Id, row.PublicStorageKey, row.UploadedAt))
                    .ToArray());
    }

    public Task<bool> IsPaymentProofFileInUseAsync(
        Guid fileId, OrderPaymentProofId? exceptProofId, CancellationToken cancellationToken)
    {
        var proofs = dbContext.OrderPaymentProofs.AsNoTracking().Where(proof => proof.FileId == fileId);
        if (exceptProofId is { } except)
        {
            proofs = proofs.Where(proof => proof.Id != except);
        }

        return proofs.AnyAsync(cancellationToken);
    }

    public void Add(Order order) => dbContext.Orders.Add(order);
}
