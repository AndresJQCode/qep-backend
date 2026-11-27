using BuildingBlocks.Domain.Catalog;
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

    public async Task<IReadOnlyDictionary<OrderId, string>> FindOrderNumbersAsync(
        Guid tenantId, IReadOnlyCollection<OrderId> orderIds, CancellationToken cancellationToken)
    {
        if (orderIds.Count == 0)
        {
            return new Dictionary<OrderId, string>();
        }

        var ids = orderIds.ToArray();
        return await dbContext.Orders
            .AsNoTracking()
            .Where(order => order.TenantId == tenantId && ids.Contains(order.Id))
            .Select(order => new { order.Id, order.OrderNumber })
            .ToDictionaryAsync(order => order.Id, order => order.OrderNumber, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<OrderId, OrderNumberAndClient>> FindNumbersAndClientsAsync(
        Guid tenantId, IReadOnlyCollection<OrderId> orderIds, CancellationToken cancellationToken)
    {
        if (orderIds.Count == 0)
        {
            return new Dictionary<OrderId, OrderNumberAndClient>();
        }

        var ids = orderIds.ToArray();
        return await dbContext.Orders
            .AsNoTracking()
            .Where(order => order.TenantId == tenantId && ids.Contains(order.Id))
            .Select(order => new { order.Id, order.OrderNumber, order.ClientId })
            .ToDictionaryAsync(
                order => order.Id, order => new OrderNumberAndClient(order.OrderNumber, order.ClientId), cancellationToken);
    }

    // La regla de Order.RequiresShipping en SQL (spec 2026-11-18 §5), como la de cotizaciones: la búsqueda del listado no
    // trae las líneas, y cargarlas para esto sería toda la página de líneas para un booleano. Un proyecto nunca se despacha
    // (spec 2026-11-26 §6.4).
    public async Task<IReadOnlySet<Guid>> FindIdsRequiringShippingAsync(
        Guid tenantId, IReadOnlyCollection<OrderId> orderIds, CancellationToken cancellationToken)
    {
        if (orderIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var ids = orderIds.ToArray();
        var found = await dbContext.Orders
            .AsNoTracking()
            .Where(order => order.TenantId == tenantId
                && ids.Contains(order.Id)
                && order.Kind != QuotationKind.Project
                && (!order.Items.Any() || order.Items.Any(item => item.Kind == ProductKind.Physical)))
            .Select(order => order.Id)
            .ToListAsync(cancellationToken);

        return found.Select(id => id.Value).ToHashSet();
    }

    public Task<Order?> FindByIdAsync(
        Guid tenantId, OrderId orderId, CancellationToken cancellationToken) =>
        WithAggregate(dbContext.Orders)
            .SingleOrDefaultAsync(
                order => order.TenantId == tenantId && order.Id == orderId,
                cancellationToken);

    public Task<Order?> FindByExternalKeyAsync(
        Guid tenantId, OrderSource source, string externalKey, CancellationToken cancellationToken) =>
        WithAggregate(dbContext.Orders)
            .SingleOrDefaultAsync(
                order => order.TenantId == tenantId && order.Source == source && order.ExternalKey == externalKey,
                cancellationToken);

    // Sin filtro de estado a propósito: anular un pedido no devuelve cupo del tope diario.
    public Task<int> CountBySourceSinceAsync(
        Guid tenantId, OrderSource source, DateTimeOffset since, CancellationToken cancellationToken) =>
        dbContext.Orders
            .AsNoTracking()
            .CountAsync(
                order => order.TenantId == tenantId && order.Source == source && order.ConvertedAt >= since,
                cancellationToken);

    public async Task<(IReadOnlyList<Order> Items, int Total)> SearchAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        OrderStatus? status,
        OrderPaymentStatus? paymentStatus,
        OrderFulfillmentStatus? fulfillmentStatus,
        bool? invoiced,
        DateOnly? orderDateFrom,
        DateOnly? orderDateTo,
        string? orderNumber,
        OrderSource? source,
        QuotationKind? kind,
        bool? wholesaleApproved,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var orders = Filtered(
            tenantId, clientId, clientIds, advisorId, status, paymentStatus, fulfillmentStatus, invoiced, orderDateFrom, orderDateTo,
            orderNumber, source, kind);
        // D-1: «Por aprobar» es la marca, no un estado; el filtro deja sólo pedidos mayoristas.
        if (wholesaleApproved is { } approved)
        {
            orders = approved
                ? orders.Where(order => order.Source == OrderSource.Wholesale && order.WholesaleApprovedAt != null)
                : orders.Where(order => order.Source == OrderSource.Wholesale && order.WholesaleApprovedAt == null);
        }

        var total = await orders.CountAsync(cancellationToken);
        // Spec 2026-11-16 §5: primero la fecha del pedido; dentro del mismo día, el instante en que se creó.
        var items = await orders
            .OrderByDescending(order => order.OrderDate)
            .ThenByDescending(order => order.ConvertedAt)
            .ThenByDescending(order => order.OrderNumber)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public Task<int> CountAwaitingWholesaleApprovalAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        OrderPaymentStatus? paymentStatus,
        OrderFulfillmentStatus? fulfillmentStatus,
        bool? invoiced,
        DateOnly? orderDateFrom,
        DateOnly? orderDateTo,
        string? orderNumber,
        OrderSource? source,
        QuotationKind? kind,
        CancellationToken cancellationToken) =>
        Filtered(
                tenantId, clientId, clientIds, advisorId, OrderStatus.Open, paymentStatus, fulfillmentStatus, invoiced,
                orderDateFrom, orderDateTo, orderNumber, source, kind)
            .Where(order => order.Source == OrderSource.Wholesale && order.WholesaleApprovedAt == null)
            .CountAsync(cancellationToken);

    public async Task<(IReadOnlyList<Order> Items, int Total)> SearchByBuyerAsync(
        Guid tenantId, Guid buyerAccountId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var orders = dbContext.Orders
            .AsNoTracking()
            .Where(order => order.TenantId == tenantId && order.BuyerAccountId == buyerAccountId);
        var total = await orders.CountAsync(cancellationToken);
        var items = await orders
            .Include(order => order.Items)
            .Include(order => order.PaymentProofs)
            .OrderByDescending(order => order.CreatedAt)
            .ThenByDescending(order => order.OrderNumber)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    // El origen y el rol se guardan como texto (HasConversion<string>), y un correo NULL nunca coincide.
    // lower(trim(...)) replica BuyerEmail.TryNormalize (Trim + ToLowerInvariant), que es como llega normalizedEmail. El trim
    // de SQL sólo quita espacios: un correo guardado con tabulador o salto de línea no coincide, que es el lado seguro (no
    // enlaza de más).
    public Task<int> LinkStoreOrdersByBillingEmailAsync(
        Guid tenantId, Guid buyerAccountId, string normalizedEmail, CancellationToken cancellationToken)
    {
        var source = nameof(OrderSource.Store);
        var billingRole = nameof(QuotationPartyRole.Billing);
        return dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE quotations.orders o
            SET buyer_account_id = {buyerAccountId}
            FROM quotations.order_parties p
            WHERE o.tenant_id = {tenantId}
              AND o.source = {source}
              AND o.buyer_account_id IS NULL
              AND NOT o.buyer_account_set_by_member
              AND p.order_id = o.id
              AND p.role = {billingRole}
              AND lower(trim(p.email)) = {normalizedEmail};
            """,
            cancellationToken);
    }

    public void ForceBuyerAccountWrite(Order order) =>
        dbContext.Entry(order).Property(value => value.BuyerAccountId).IsModified = true;

    public Task<Order?> FindByBuyerAsync(
        Guid tenantId, Guid buyerAccountId, OrderId orderId, CancellationToken cancellationToken) =>
        WithAggregate(dbContext.Orders)
            .SingleOrDefaultAsync(
                order => order.TenantId == tenantId
                    && order.BuyerAccountId == buyerAccountId
                    && order.Id == orderId,
                cancellationToken);

    public Task<bool> AnyForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        OrderStatus? status,
        OrderPaymentStatus? paymentStatus,
        OrderFulfillmentStatus? fulfillmentStatus,
        bool? invoiced,
        DateOnly? orderDateFrom,
        DateOnly? orderDateTo,
        string? orderNumber,
        OrderSource? source,
        QuotationKind? kind,
        CancellationToken cancellationToken) =>
        Filtered(
                tenantId, clientId, clientIds, advisorId, status, paymentStatus, fulfillmentStatus, invoiced, orderDateFrom,
                orderDateTo, orderNumber, source, kind)
            .AnyAsync(cancellationToken);

    public async Task<IReadOnlyList<Order>> ListForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        OrderStatus? status,
        OrderPaymentStatus? paymentStatus,
        OrderFulfillmentStatus? fulfillmentStatus,
        bool? invoiced,
        DateOnly? orderDateFrom,
        DateOnly? orderDateTo,
        string? orderNumber,
        OrderSource? source,
        QuotationKind? kind,
        OrderExportCursor? after,
        int limit,
        CancellationToken cancellationToken)
    {
        var orders = Filtered(
            tenantId, clientId, clientIds, advisorId, status, paymentStatus, fulfillmentStatus, invoiced, orderDateFrom, orderDateTo,
            orderNumber, source, kind);
        if (after is not null)
        {
            // El cursor es el orden del listado entero (spec 2026-11-16 §5): fecha del pedido, instante y número.
            var orderDate = after.OrderDate;
            var convertedAt = after.ConvertedAt;
            var number = after.OrderNumber;
            orders = orders.Where(order => EF.Functions.LessThan(
                ValueTuple.Create(order.OrderDate, order.ConvertedAt, order.OrderNumber),
                ValueTuple.Create(orderDate, convertedAt, number)));
        }

        return await orders
            .OrderByDescending(order => order.OrderDate)
            .ThenByDescending(order => order.ConvertedAt)
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
        OrderFulfillmentStatus? fulfillmentStatus,
        bool? invoiced,
        DateOnly? orderDateFrom,
        DateOnly? orderDateTo,
        string? orderNumber,
        OrderSource? source,
        QuotationKind? kind)
    {
        var orders = dbContext.Orders
            .AsNoTracking()
            .Where(order => order.TenantId == tenantId);
        // Spec 2026-11-26 §6.6: sin valor salen los dos tipos.
        if (kind is { } orderKind)
        {
            orders = orders.Where(order => order.Kind == orderKind);
        }

        // Sin filtro de estado, los archivados no salen (spec 2026-11-10 §5): el listado es lo pendiente de trabajar.
        orders = status is { } orderStatus
            ? orders.Where(order => order.Status == orderStatus)
            : orders.Where(order => order.Status != OrderStatus.Archived);

        if (fulfillmentStatus is { } orderFulfillmentStatus)
        {
            orders = orders.Where(order => order.FulfillmentStatus == orderFulfillmentStatus);
        }

        if (invoiced is { } isInvoiced)
        {
            orders = isInvoiced
                ? orders.Where(order => order.InvoicedAt != null)
                : orders.Where(order => order.InvoicedAt == null);
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

        // Spec 2026-11-16 §4: el filtro de fechas corta por la fecha del pedido, inclusive en las dos puntas y sin huso.
        if (orderDateFrom is { } from)
        {
            orders = orders.Where(order => order.OrderDate >= from);
        }

        if (orderDateTo is { } to)
        {
            orders = orders.Where(order => order.OrderDate <= to);
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

    public async Task<IReadOnlySet<Guid>> ListPaymentProofFilesInUseAsync(
        IReadOnlyCollection<Guid> fileIds, OrderPaymentProofId? exceptProofId, CancellationToken cancellationToken)
    {
        if (fileIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        // Un arreglo, no la colección: Npgsql lo manda como un solo parámetro (file_id = ANY (@ids)).
        var ids = fileIds.Distinct().ToArray();
        var proofs = dbContext.OrderPaymentProofs.AsNoTracking().Where(proof => ids.Contains(proof.FileId));
        if (exceptProofId is { } except)
        {
            proofs = proofs.Where(proof => proof.Id != except);
        }

        var inUse = await proofs.Select(proof => proof.FileId).Distinct().ToListAsync(cancellationToken);
        return inUse.ToHashSet();
    }

    public void Add(Order order) => dbContext.Orders.Add(order);

    public void AddHistoryEntry(OrderHistoryEntry entry) => dbContext.OrderHistoryEntries.Add(entry);
}
