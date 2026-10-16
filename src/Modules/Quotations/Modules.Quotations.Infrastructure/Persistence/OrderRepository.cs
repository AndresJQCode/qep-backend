using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Infrastructure.Persistence;

internal sealed class OrderRepository(QuotationsDbContext dbContext) : IOrderRepository
{
    private const string LikeEscapeCharacter = "\\";

    private static string EscapeLikeWildcards(string term) => term
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static string? LikePattern(string? term)
    {
        var trimmed = term?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : $"%{EscapeLikeWildcards(trimmed)}%";
    }

    // El agregado entero: líneas, partes y comprobantes. Lo que lee un pedido para mostrarlo o
    // editarlo necesita las tres.
    private static IQueryable<Order> WithAggregate(IQueryable<Order> orders) =>
        orders
            .Include(order => order.PaymentProofs)
            .Include(order => order.Items)
            .Include(order => order.Parties);

    public Task<Order?> FindByQuotationIdAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken) =>
        WithAggregate(dbContext.Orders)
            .SingleOrDefaultAsync(
                order => order.TenantId == tenantId && order.QuotationId == quotationId,
                cancellationToken);

    public Task<Order?> FindUntrackedByIdAsync(
        Guid tenantId, OrderId orderId, CancellationToken cancellationToken) =>
        WithAggregate(dbContext.Orders.AsNoTracking())
            .SingleOrDefaultAsync(
                order => order.TenantId == tenantId && order.Id == orderId,
                cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, Order>> FindByQuotationIdsAsync(
        Guid tenantId,
        IReadOnlyCollection<QuotationId> quotationIds,
        CancellationToken cancellationToken)
    {
        if (quotationIds.Count == 0)
        {
            return new Dictionary<Guid, Order>();
        }

        var ids = quotationIds.Select(id => (QuotationId?)id).ToArray();
        var orders = await dbContext.Orders
            .AsNoTracking()
            .Where(order => order.TenantId == tenantId && order.QuotationId != null && ids.Contains(order.QuotationId))
            .ToListAsync(cancellationToken);
        return orders.ToDictionary(order => order.QuotationId!.Value.Value);
    }

    public Task<Order?> FindByIdAsync(
        Guid tenantId, OrderId orderId, CancellationToken cancellationToken) =>
        WithAggregate(dbContext.Orders)
            .SingleOrDefaultAsync(
                order => order.TenantId == tenantId && order.Id == orderId,
                cancellationToken);

    public async Task<(IReadOnlyList<Order> Items, int Total)> SearchAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        OrderStatus? status,
        OrderPaymentStatus? paymentStatus,
        DateTimeOffset? convertedFrom,
        DateTimeOffset? convertedBefore,
        string? orderNumber,
        OrderSource? source,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var orders = Filtered(
            tenantId, clientId, clientIds, advisorId, status, paymentStatus, convertedFrom, convertedBefore, orderNumber, source);
        var total = await orders.CountAsync(cancellationToken);
        var items = await orders
            .OrderByDescending(order => order.ConvertedAt)
            .ThenByDescending(order => order.OrderNumber)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
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
        OrderSource? source,
        CancellationToken cancellationToken) =>
        Filtered(tenantId, clientId, clientIds, advisorId, status, paymentStatus, convertedFrom, convertedBefore, orderNumber, source)
            .AnyAsync(cancellationToken);

    public async Task<IReadOnlyList<Order>> ListForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        OrderStatus? status,
        OrderPaymentStatus? paymentStatus,
        DateTimeOffset? convertedFrom,
        DateTimeOffset? convertedBefore,
        string? orderNumber,
        OrderSource? source,
        OrderExportCursor? after,
        int limit,
        CancellationToken cancellationToken)
    {
        var orders = Filtered(
            tenantId, clientId, clientIds, advisorId, status, paymentStatus, convertedFrom, convertedBefore, orderNumber, source);
        if (after is not null)
        {
            var convertedAt = after.ConvertedAt;
            var number = after.OrderNumber;
            orders = orders.Where(order => EF.Functions.LessThan(
                ValueTuple.Create(order.ConvertedAt, order.OrderNumber),
                ValueTuple.Create(convertedAt, number)));
        }

        return await orders
            .OrderByDescending(order => order.ConvertedAt)
            .ThenByDescending(order => order.OrderNumber)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    // Spec 2026-10-15 §6.1: todos los filtros, también cliente, CUC (ids de cliente) y asesor, son
    // columnas del pedido.
    private IQueryable<Order> Filtered(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        OrderStatus? status,
        OrderPaymentStatus? paymentStatus,
        DateTimeOffset? convertedFrom,
        DateTimeOffset? convertedBefore,
        string? orderNumber,
        OrderSource? source)
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

        if (source is { } orderSource)
        {
            orders = orders.Where(order => order.Source == orderSource);
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
            orders = orders.Where(order => order.ConvertedAt < before);
        }

        if (clientId is { } client)
        {
            orders = orders.Where(order => order.ClientId == client);
        }

        if (clientIds is not null)
        {
            orders = orders.Where(order => clientIds.Contains(order.ClientId));
        }

        if (advisorId is { } advisor)
        {
            orders = orders.Where(order => order.AdvisorId == advisor);
        }

        return orders;
    }

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
                select new { proof.OrderId, proof.Id, proof.PublicStorageKey, proof.UploadedAt, proof.Amount, proof.PaidOn })
            .ToListAsync(cancellationToken);
        return rows
            .GroupBy(row => row.OrderId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<OrderExportPaymentProof>)group
                    .Select(row => new OrderExportPaymentProof(
                        row.Id, row.PublicStorageKey, row.UploadedAt, row.Amount, row.PaidOn))
                    .ToArray());
    }

    public async Task<IReadOnlyDictionary<OrderId, IReadOnlyList<OrderItem>>> ListItemsForExportAsync(
        Guid tenantId,
        IReadOnlyCollection<OrderId> orderIds,
        CancellationToken cancellationToken)
    {
        if (orderIds.Count == 0)
        {
            return new Dictionary<OrderId, IReadOnlyList<OrderItem>>();
        }

        var ids = orderIds.ToArray();
        var items = await (
                from item in dbContext.OrderItems.AsNoTracking()
                join order in dbContext.Orders.AsNoTracking() on item.OrderId equals order.Id
                where order.TenantId == tenantId && ids.Contains(order.Id)
                orderby item.Position
                select item)
            .ToListAsync(cancellationToken);
        return items
            .GroupBy(item => item.OrderId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<OrderItem>)group.ToArray());
    }

    public async Task<IReadOnlyDictionary<OrderId, IReadOnlyList<OrderParty>>> ListPartiesForExportAsync(
        Guid tenantId,
        IReadOnlyCollection<OrderId> orderIds,
        CancellationToken cancellationToken)
    {
        if (orderIds.Count == 0)
        {
            return new Dictionary<OrderId, IReadOnlyList<OrderParty>>();
        }

        var ids = orderIds.ToArray();
        var parties = await (
                from party in dbContext.OrderParties.AsNoTracking()
                join order in dbContext.Orders.AsNoTracking() on party.OrderId equals order.Id
                where order.TenantId == tenantId && ids.Contains(order.Id)
                select party)
            .ToListAsync(cancellationToken);
        return parties
            .GroupBy(party => party.OrderId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<OrderParty>)group.ToArray());
    }

    public async Task<IReadOnlyList<OrderHistoryEntry>> ListHistoryAsync(
        Guid tenantId, OrderId orderId, CancellationToken cancellationToken) =>
        await (
                from entry in dbContext.OrderHistoryEntries.AsNoTracking()
                join order in dbContext.Orders.AsNoTracking() on entry.OrderId equals order.Id
                where order.TenantId == tenantId && order.Id == orderId
                orderby entry.EventAt descending, entry.CreatedAt descending
                select entry)
            .ToListAsync(cancellationToken);

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

    public void AddHistoryEntry(OrderHistoryEntry entry) => dbContext.OrderHistoryEntries.Add(entry);
}
