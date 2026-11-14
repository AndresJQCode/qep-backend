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
/// <item>Antes de reemplazar el abierto, lo asienta (<see cref="SettleAsync"/>): ver abajo.</item>
/// <item>Sin grupos (nada cambió o todo es eco): reemplaza el abierto y adopta el snapshot como versión aplicada.</item>
/// <item>Con grupos: reemplaza el abierto y crea el cambio <c>Blocked</c> (con su motivo) o <c>Pending</c>.</item>
/// </list>
/// Con la tienda automática, el pendiente se aplica en el mismo procesamiento pero <b>después</b> del guardado de la ingesta
/// (<see cref="ApplyPendingAsync"/>). El pedido cambia en Quotations, que confirma por su cuenta (review I-1, N-1, N-2):
/// <list type="bullet">
/// <item>Si algo falla antes de ese guardado (la reacción de §6.3, la fila del espejo), el pedido no se tocó y el reintento vuelve a
/// detectar desde el mismo estado.</item>
/// <item>Si Quotations confirmó y el guardado del cambio (automático o al aceptar) falló, el cambio sigue guardado abierto con sus
/// grupos, y lo que aplicaba ya está en el pedido. Lo termine quien lo termine, esos grupos salen como eco al volver a planear su
/// snapshot y, como el cambio los aplicaba, la proyección toma el valor del pedido: el reintento o un nuevo aceptar
/// (<see cref="ApplyAsync"/>, también cuando el pedido ya no se puede editar: <see cref="OnlySettles"/>), y el rechazo, la revisión
/// o el reemplazo por un snapshot más nuevo (<see cref="SettleAsync"/>). No se aplica dos veces y la edición siguiente de ese grupo
/// no es un conflicto falso.</item>
/// <item>Un eco que el cambio no aplicaba (la corrección del asesor que vuelve de Shopify, Review Focus 5) nunca mueve la
/// proyección.</item>
/// </list>
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
        if (row.AppliedPayload is null)
        {
            // Plan D6: la base de una fila importada antes de la sincronización es el payload que tenía. Se guarda como versión
            // aplicada para que aceptar el cambio después planee contra la misma base (la fila ya tiene el snapshot nuevo).
            row.AdoptPayload(previousPayload);
        }

        if (open is not null)
        {
            // Review N-1: el abierto pudo haber confirmado en Quotations sin que su guardado entrara. Antes de reemplazarlo, lo que
            // aplicaba y el pedido ya tiene pasa a la proyección, y el snapshot nuevo se planea contra ella.
            await SettleAsync(store, row, open, order, cancellationToken);
        }

        var applied = ShopifyOrderIngestion.ReadPayload(row.AppliedPayload!);
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
    }

    /// <summary>
    /// Con la tienda automática, aplica el cambio pendiente del snapshot vigente de la fila. La ingesta lo llama después de guardar
    /// el cambio, así que el pedido de QEP nunca cambia sin un cambio guardado que lo explique. Vuelve a planear contra el pedido de
    /// ahora: la reacción de §6.3 pudo haberlo movido, o es el reintento de una aplicación cuyo guardado cayó (review I-1).
    /// </summary>
    public async Task<bool> ApplyPendingAsync(ShopifyStore store, ShopifyOrderMirror row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(row);
        if (store.EditSync != ShopifyEditSync.Automatic
            || row.ImportState != ShopifyImportState.Imported
            || row.QepOrderId is not { } orderId
            || row.AppliedPayload is null)
        {
            return false;
        }

        var edit = await edits.FindOpenAsync(row.TenantId, row.Id, cancellationToken);
        if (edit is not { Status: ShopifyOrderEditStatus.Pending } || edit.SnapshotUpdatedAt != row.ShopifyUpdatedAt)
        {
            return false;
        }

        if (await orders.ReadStateAsync(row.TenantId, orderId, cancellationToken) is not { } order)
        {
            return false;
        }

        var applied = ShopifyOrderIngestion.ReadPayload(row.AppliedPayload);
        var incoming = ShopifyOrderIngestion.ReadPayload(edit.Payload);
        var projection = ProjectionOf(row, store, applied, order);
        var plan = await planner.PlanAsync(store, row, applied, projection, incoming, order, row.QepAttributes, cancellationToken);
        var now = clock.UtcNow;
        if (plan.BlockReason is { } reason && !OnlySettles(edit, plan))
        {
            edit.Block(reason, plan.IsEmpty ? edit.Groups : WriteGroups(plan.Groups), now);
            return true;
        }

        try
        {
            if (await ApplyAsync(store, row, edit, plan, projection, incoming, order, null, cancellationToken) is { } rejected
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

        return true;
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
    /// proyección (§4):
    /// <list type="bullet">
    /// <item>De los grupos aplicados, lo que quedó guardado en el pedido.</item>
    /// <item>De los que este mismo cambio aplicaba (<c>Apply</c> en sus grupos guardados) y ahora son eco, el valor del pedido
    /// (<paramref name="order"/>): ya está en el pedido porque una aplicación anterior de este cambio confirmó en Quotations y su
    /// guardado cayó, o porque el asesor hizo a mano lo mismo que Shopify pedía. Sin esto la proyección quedaría vieja y cada edición
    /// siguiente de ese grupo sería un <c>qep_changed</c> falso (review I-1). Un eco que el cambio no aplicaba (la corrección del
    /// asesor que vuelve de Shopify, Review Focus 5) no mueve la proyección.</item>
    /// <item>De los demás (conflicto, eco ajeno), lo que ya había.</item>
    /// </list>
    /// Devuelve el código con que Quotations lo rechazó, o <c>null</c> si se aplicó. <c>concurrency.conflict</c> se propaga.
    /// </summary>
    public async Task<string?> ApplyAsync(
        ShopifyStore store,
        ShopifyOrderMirror row,
        ShopifyOrderEdit edit,
        ShopifyEditPlan plan,
        ShopifyOrderProjection projection,
        ShopifyOrderSnapshot incoming,
        ShopifyOrderState order,
        Guid? decidedBy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(order);
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

        var settled = SettledGroups(edit, plan);
        edit.MarkApplied(GroupsOnceApplied(edit, plan, settled), decidedBy, clock.UtcNow);
        row.RecordApplied(
            ShopifyOrderIngestion.WritePayload(incoming),
            ShopifyOrderProjection.Write(Merge(projection, state ?? order, plan, [.. settled.Select(group => group.Kind)], incoming, store)));
        return null;
    }

    /// <summary>
    /// Review N-2: el plan no tiene nada que escribir, pero sí grupos que el cambio aplicaba y el pedido ya tiene. Es el reintento
    /// de una aplicación que confirmó en Quotations aunque el pedido ya no se pueda editar o su pago haya cambiado: se termina
    /// (<see cref="ApplyAsync"/> sin escribir en el pedido) en vez de bloquearlo con la proyección vieja.
    /// </summary>
    public static bool OnlySettles(ShopifyOrderEdit edit, ShopifyEditPlan plan)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(plan);
        return !plan.Groups.Any(group => group.Outcome == ShopifyEditGroupOutcome.Apply) && SettledGroups(edit, plan).Length > 0;
    }

    /// <summary>
    /// Review N-1 y N-2: un cambio que se cierra sin aplicarse (lo reemplaza un snapshot más nuevo, o se rechaza o revisa) pudo
    /// haber confirmado en Quotations sin que su guardado entrara. Se planea su propio snapshot contra el pedido de ahora y, de los
    /// grupos que aplicaba, los que el pedido ya tiene pasan a la proyección. Los ecos que no aplicaba no la mueven (Review Focus 5).
    /// Una fila sin proyección (plan D6) ya se compara contra el pedido de ahora y no se toca.
    /// </summary>
    public async Task SettleAsync(
        ShopifyStore store, ShopifyOrderMirror row, ShopifyOrderEdit edit, ShopifyOrderState order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(edit);
        if (row is not { AppliedPayload: { } appliedJson, AppliedProjection: { } projectionJson }
            || !ReadGroups(edit.Groups).Any(group => group.Outcome == ShopifyEditGroupOutcome.Apply))
        {
            return;
        }

        var projection = ShopifyOrderProjection.Read(projectionJson);
        var incoming = ShopifyOrderIngestion.ReadPayload(edit.Payload);
        var plan = await planner.PlanAsync(
            store, row, ShopifyOrderIngestion.ReadPayload(appliedJson), projection, incoming, order, row.QepAttributes, cancellationToken);
        var settled = SettledGroups(edit, plan).Select(group => group.Kind).ToHashSet();
        if (settled.Count > 0)
        {
            row.RecordProjection(ShopifyOrderProjection.Write(Move(projection, order, settled.Contains, incoming.Attributes)));
        }
    }

    // Los grupos que el cambio guardado aplicaba (Apply) y que el plan de ahora da como eco: el pedido ya tiene ese valor.
    private static ShopifyEditGroup[] SettledGroups(ShopifyOrderEdit edit, ShopifyEditPlan plan) =>
        [.. ReadGroups(edit.Groups).Where(group => group.Outcome == ShopifyEditGroupOutcome.Apply && plan.Echoes.Contains(group.Kind))];

    private static List<ShopifyEditGroup> ReadGroups(string groupsJson) =>
        JsonSerializer.Deserialize<List<ShopifyEditGroup>>(groupsJson, GroupsJson) ?? [];

    // Lo que el cambio muestra ya aplicado: los grupos del plan y, de los que ya estaban en el pedido, los que el cambio aplicaba. Si
    // todo quedó como eco y nada lo aplicaba (el asesor ya lo había hecho a mano), conserva lo que mostraba.
    private static string GroupsOnceApplied(ShopifyOrderEdit edit, ShopifyEditPlan plan, ShopifyEditGroup[] settled)
    {
        if (settled.Length == 0)
        {
            return plan.IsEmpty ? edit.Groups : WriteGroups(plan.Groups);
        }

        return WriteGroups([.. plan.Groups.Concat(settled).OrderBy(group => group.Kind)]);
    }

    private static ShopifyOrderProjection Merge(
        ShopifyOrderProjection projection,
        ShopifyOrderState current,
        ShopifyEditPlan plan,
        IReadOnlyCollection<ShopifyEditGroupKind> settled,
        ShopifyOrderSnapshot incoming,
        ShopifyStore store)
    {
        bool Applied(ShopifyEditGroupKind kind) =>
            settled.Contains(kind)
            || plan.Groups.Any(group => group.Kind == kind && group.Outcome == ShopifyEditGroupOutcome.Apply);

        // Un grupo sólo sale Apply en el plan si su escritura entró en el pedido (con borrador) o no toca el pedido (atributos).
        return Move(projection, current, Applied, incoming.Attributes) with { Document = ShopifyCustomerResolver.DocumentOf(store, incoming) };
    }

    // Los grupos que pasan a la proyección toman el valor del pedido (los atributos, que no viven en el pedido, los del snapshot).
    private static ShopifyOrderProjection Move(
        ShopifyOrderProjection projection,
        ShopifyOrderState current,
        Func<ShopifyEditGroupKind, bool> moves,
        IReadOnlyDictionary<string, string> attributes) =>
        projection with
        {
            Lines = moves(ShopifyEditGroupKind.Lines) ? current.Lines : projection.Lines,
            Shipping = moves(ShopifyEditGroupKind.Shipping) ? current.Shipping : projection.Shipping,
            ShippingParty = moves(ShopifyEditGroupKind.ShippingAddress) ? current.ShippingParty : projection.ShippingParty,
            BuyerNotes = moves(ShopifyEditGroupKind.Note) ? current.BuyerNotes : projection.BuyerNotes,
            Attributes = moves(ShopifyEditGroupKind.Attributes) ? attributes : projection.Attributes,
        };
}
