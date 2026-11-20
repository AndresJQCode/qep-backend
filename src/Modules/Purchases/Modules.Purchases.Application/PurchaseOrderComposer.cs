using Modules.Purchases.Domain;
using Modules.Tenancy.Application;

namespace Modules.Purchases.Application;

/// <summary>
/// Arma los DTOs del contrato. Proveedores y nombres de miembros se resuelven en lote (una llamada por puerto, nunca una por
/// orden), y <c>allowedActions</c> cruza el estado con el permiso de gestionar (D-4).
/// </summary>
public sealed class PurchaseOrderComposer(
    IExecutionContext executionContext,
    IPurchasesSupplierLookup suppliers,
    IPurchasesMemberDirectory members)
{
    /// <summary>Lo que se muestra cuando la membresía ya no existe o el proveedor no está en el directorio.</summary>
    public const string UnknownName = "Sin nombre";

    /// <summary>D-19: el autor de la confirmación y la anulación que llegan del mayorista (sin miembro del comprador).</summary>
    public const string WholesalerName = "El mayorista";

    public async Task<PurchaseOrderDetailDto> DetailAsync(PurchaseOrder order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);

        var supplierMap = await suppliers.FindManyAsync(order.TenantId, [order.SupplierId], cancellationToken);
        var memberIds = order.History
            .Select(entry => entry.MemberId)
            .Append(order.CreatedByMemberId)
            .Distinct()
            .ToArray();
        var names = await members.FindNamesAsync(order.TenantId, memberIds, cancellationToken);

        // Un proveedor que no está es una referencia blanda rota (Suppliers no borra): se muestra sin nombre y como inactivo
        // antes que tumbar el detalle con un 500.
        var supplier = supplierMap.TryGetValue(order.SupplierId, out var found)
            ? new PurchaseOrderSupplierDto(found.Id, found.Name, found.IsActive, found.LinkedTenantId is not null)
            : new PurchaseOrderSupplierDto(order.SupplierId, UnknownName, false);
        var totals = order.Totals();

        return new PurchaseOrderDetailDto(
            order.Id.Value,
            order.Number,
            order.Status.ToString(),
            order.ConfirmedWithChanges,
            order.Version,
            supplier,
            order.Currency,
            order.Notes,
            new PurchaseOrderMemberDto(order.CreatedByMemberId, NameOf(names, order.CreatedByMemberId)),
            order.CreatedAt,
            order.Lines.Select(ToLine).ToList(),
            totals is null ? null : ToTotals(totals),
            order.RequestedTotals()?.Total,
            order.History
                .OrderBy(entry => entry.Sequence)
                .Select(entry => new PurchaseOrderHistoryDto(
                    entry.Status.ToString(),
                    entry.OccurredAt,
                    order.IsLinked && entry.MemberId == Guid.Empty ? WholesalerName : NameOf(names, entry.MemberId)))
                .ToList(),
            AllowedActions(order),
            ModeOf(order),
            order.LinkedOrderNumber is { } linkedOrderNumber ? new PurchaseOrderLinkedDto(linkedOrderNumber) : null,
            new PurchaseOrderChangesDto(
                order.Lines.Count(line => line.Status == PurchaseOrderLineStatus.Changed),
                order.Lines.Count(line => line.Status == PurchaseOrderLineStatus.Added),
                order.Lines.Count(line => line.Status == PurchaseOrderLineStatus.Rejected),
                order.Lines.Count(line => line.CreatedInCatalog)),
            new PurchaseOrderEmailDto(
                order.LastEmailStatus?.ToString(),
                order.LastEmailTo,
                order.LastEmailAt,
                supplierMap.TryGetValue(order.SupplierId, out var emailSupplier) ? emailSupplier.Email : null),
            order.HasPrices);
    }

    public async Task<IReadOnlyList<PurchaseOrderSummaryDto>> SummariesAsync(
        Guid tenantId, IReadOnlyList<PurchaseOrder> orders, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(orders);
        if (orders.Count == 0)
        {
            return [];
        }

        var supplierIds = orders.Select(order => order.SupplierId).Distinct().ToArray();
        var supplierMap = await suppliers.FindManyAsync(tenantId, supplierIds, cancellationToken);

        return orders
            .Select(order => new PurchaseOrderSummaryDto(
                order.Id.Value,
                order.Number,
                order.Status.ToString(),
                order.ConfirmedWithChanges,
                new PurchaseOrderSupplierRefDto(
                    order.SupplierId,
                    supplierMap.TryGetValue(order.SupplierId, out var supplier) ? supplier.Name : UnknownName),
                order.Currency,
                order.Totals()?.Total,
                order.CreatedAt,
                order.Lines.Count,
                order.IsLinked,
                ModeOf(order),
                order.HasPrices))
            .ToList();
    }

    internal static PurchaseOrderTotalsDto ToTotals(PurchaseOrderTotals totals) =>
        new(totals.Subtotal, totals.Tax, totals.Total);

    /// <summary>Reenviar el correo no es una transición del dominio: se suma al final cuando la orden ya salió y no está cancelada.</summary>
    public const string ResendEmailAction = "resend_email";

    private List<string> AllowedActions(PurchaseOrder order)
    {
        if (!executionContext.HasPermission(PurchasesPermissions.OrderManage))
        {
            return [];
        }

        var actions = order.AllowedActions().Select(action => action.ToString().ToLowerInvariant()).ToList();
        if (order.CanEmailSupplier)
        {
            actions.Add(ResendEmailAction);
        }

        return actions;
    }

    private static PurchaseOrderLineDto ToLine(PurchaseOrderLine line) => new(
        line.Id,
        line.VariantId,
        line.Code,
        line.Name,
        line.Quantity,
        line.ConfirmedQuantity,
        line.UnitCost,
        line.ConfirmedUnitCost,
        line.TaxRatePercent,
        line.Status.ToString(),
        line.Subtotal,
        line.SupplierVariantId,
        line is { ListBasePrice: { } basePrice, ListDiscountPercent: { } discount, ListUnitPrice: { } unitPrice }
            ? new PurchaseOrderListPriceDto(basePrice, discount, unitPrice)
            : null,
        line.CreatedInCatalog);

    private static string ModeOf(PurchaseOrder order) =>
        order.IsLinked ? PurchaseOrderModes.Linked : PurchaseOrderModes.External;

    private static string NameOf(IReadOnlyDictionary<Guid, string> names, Guid memberId) =>
        names.TryGetValue(memberId, out var name) && !string.IsNullOrWhiteSpace(name) ? name : UnknownName;
}
