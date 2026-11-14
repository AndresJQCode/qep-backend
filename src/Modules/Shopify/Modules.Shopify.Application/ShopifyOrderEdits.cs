using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using BuildingBlocks.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-11-14 §3 y §4: el detector de ediciones que la ingesta llama sobre una fila que ya existía, después de
/// <c>ApplySnapshot</c> y antes de la reacción de pago y cancelación (§6.3). Sólo mira filas que ya estaban <c>Imported</c>, con
/// pedido, y snapshots sin cancelar (plan D7: la cancelación manda).
/// <list type="number">
/// <item>La misma entrega otra vez (el cambio abierto ya es de ese <c>updated_at</c>) no hace nada.</item>
/// <item>La base es la versión aplicada; en una fila importada antes de este cambio, el payload que la fila tenía (plan D6), y la
/// proyección sale del pedido de ahora: nada cuenta como «lo tocó QEP».</item>
/// <item>Sin grupos (nada cambió o todo es eco): reemplaza el abierto y adopta el snapshot como versión aplicada.</item>
/// <item>Con grupos: reemplaza el abierto y crea el cambio <c>Blocked</c> (con su motivo), <c>Pending</c> o, con la tienda
/// automática, lo aplica en el mismo procesamiento.</item>
/// </list>
/// El pedido cambia en Quotations, que confirma por su cuenta; la fila y el cambio entran en el guardado de la ingesta. Si ésta no
/// guarda y reintenta, el pedido ya cambiado es eco del snapshot y no se aplica dos veces.
/// </summary>
public sealed class ShopifyOrderEdits(
    ShopifyEditPlanner planner, IShopifyOrderWriter orders, IShopifyOrderEditRepository edits, IClock clock) : IShopifyOrderEditDetector
{
    /// <summary>Lo devuelve Quotations cuando el pedido ya no se edita (preparado, facturado, anulado).</summary>
    public const string OrderNotEditableCode = "order.order.not_editable";

    private const string ConcurrencyConflict = "concurrency.conflict";

    // Los grupos viajan a la pantalla tal como se guardan: camelCase, enums por nombre y las tildes tal cual (BFF).
    private static readonly JsonSerializerOptions GroupsJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter() },
    };

    public static string WriteGroups(IReadOnlyList<ShopifyEditGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        return JsonSerializer.Serialize(groups, GroupsJson);
    }

    public async Task DetectAsync(ShopifyOrderChange change, string previousPayload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var row = change.Row;
        var snapshot = change.Snapshot;
        if (change.Previous.ImportState != ShopifyImportState.Imported || row.QepOrderId is not { } orderId || snapshot.CancelledAt is not null)
        {
            return;
        }

        var open = await edits.FindOpenAsync(row.TenantId, row.Id, cancellationToken);
        if (open is not null && open.SnapshotUpdatedAt == snapshot.UpdatedAt)
        {
            return;
        }

        if (await orders.ReadStateAsync(row.TenantId, orderId, cancellationToken) is not { } order)
        {
            return;
        }

        var store = change.Store;
        var applied = ShopifyOrderIngestion.ReadPayload(row.AppliedPayload ?? previousPayload);
        var projection = ProjectionOf(row, store, applied, order);
        var plan = await planner.PlanAsync(store, row, applied, projection, snapshot, order, row.QepAttributes, cancellationToken);
        var now = clock.UtcNow;
        open?.Supersede(now);
        if (plan.IsEmpty)
        {
            row.AdoptPayload(ShopifyOrderIngestion.WritePayload(snapshot));
            return;
        }

        var edit = ShopifyOrderEdit.Create(
            row.TenantId,
            row.ConnectionId,
            row.Id,
            orderId,
            applied.UpdatedAt,
            snapshot.UpdatedAt,
            ShopifyOrderIngestion.WritePayload(snapshot),
            WriteGroups(plan.Groups),
            plan.BlockReason is null ? ShopifyOrderEditStatus.Pending : ShopifyOrderEditStatus.Blocked,
            plan.BlockReason,
            now);
        edits.Add(edit);
        if (plan.BlockReason is not null || store.EditSync != ShopifyEditSync.Automatic)
        {
            return;
        }

        try
        {
            if (await ApplyAsync(store, row, edit, plan, projection, snapshot, null, cancellationToken) is { } rejected
                && string.Equals(rejected, OrderNotEditableCode, StringComparison.Ordinal))
            {
                edit.Block(ShopifyEditReasons.OrderNotEditable, edit.Groups, now);
            }
        }
        catch (RequestConcurrencyException exception) when (string.Equals(exception.Code, ConcurrencyConflict, StringComparison.Ordinal))
        {
            // El pedido cambió entre el plan y la escritura (lo editó el asesor): el cambio queda pendiente y no sube al bucle de la
            // ingesta, que reintentaría la fila del espejo por un conflicto que no es suyo.
        }
    }

    /// <summary>
    /// La proyección contra la que se decide: la guardada, o (fila importada antes de este cambio, plan D6) la del pedido de ahora.
    /// </summary>
    public static ShopifyOrderProjection ProjectionOf(
        ShopifyOrderMirror row, ShopifyStore store, ShopifyOrderSnapshot applied, ShopifyOrderState order)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row is { AppliedPayload: not null, AppliedProjection: { } projection }
            ? ShopifyOrderProjection.Read(projection)
            : ShopifyOrderProjection.From(order, ShopifyCustomerResolver.DocumentOf(store, applied), applied.Attributes);
    }

    /// <summary>
    /// Escribe los grupos que se aplican y, si Quotations los acepta, marca el cambio aplicado y mueve la versión aplicada y la
    /// proyección: de los grupos aplicados, lo que quedó guardado en el pedido; de los demás (conflicto, eco), lo que ya había (§4).
    /// Devuelve el código con que Quotations lo rechazó, o <c>null</c> si se aplicó. <c>concurrency.conflict</c> se propaga.
    /// </summary>
    public async Task<string?> ApplyAsync(
        ShopifyStore store,
        ShopifyOrderMirror row,
        ShopifyOrderEdit edit,
        ShopifyEditPlan plan,
        ShopifyOrderProjection projection,
        ShopifyOrderSnapshot incoming,
        Guid? decidedBy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(plan);
        ShopifyOrderState? state = null;
        if (plan.Draft is { } draft)
        {
            var outcome = await orders.ApplyEditAsync(draft, cancellationToken);
            if (outcome.State is null)
            {
                return outcome.RejectedCode ?? OrderNotEditableCode;
            }

            state = outcome.State;
        }

        edit.MarkApplied(WriteGroups(plan.Groups), decidedBy, clock.UtcNow);
        row.RecordApplied(
            ShopifyOrderIngestion.WritePayload(incoming),
            ShopifyOrderProjection.Write(Merge(projection, state, plan, incoming, store)));
        return null;
    }

    private static ShopifyOrderProjection Merge(
        ShopifyOrderProjection projection, ShopifyOrderState? state, ShopifyEditPlan plan, ShopifyOrderSnapshot incoming, ShopifyStore store)
    {
        bool Applied(ShopifyEditGroupKind kind) =>
            plan.Groups.Any(group => group.Kind == kind && group.Outcome == ShopifyEditGroupOutcome.Apply);

        return new ShopifyOrderProjection(
            state is not null && Applied(ShopifyEditGroupKind.Lines) ? state.Lines : projection.Lines,
            state is not null && Applied(ShopifyEditGroupKind.Shipping) ? state.Shipping : projection.Shipping,
            state is not null && Applied(ShopifyEditGroupKind.ShippingAddress) ? state.ShippingParty : projection.ShippingParty,
            state is not null && Applied(ShopifyEditGroupKind.Note) ? state.BuyerNotes : projection.BuyerNotes,
            ShopifyCustomerResolver.DocumentOf(store, incoming),
            Applied(ShopifyEditGroupKind.Attributes) ? incoming.Attributes : projection.Attributes);
    }
}
