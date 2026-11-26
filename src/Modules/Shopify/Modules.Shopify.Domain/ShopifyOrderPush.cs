namespace Modules.Shopify.Domain;

/// <summary>Spec 2026-11-14 §5: lo que viaja a Shopify. Se guarda por nombre; el orden es el del cuerpo de <c>orderUpdate</c>.</summary>
public enum ShopifyPushGroup
{
    ShippingAddress,
    Note,
    Attributes,
}

/// <summary>Spec 2026-11-14 §5: el estado de un envío. Se guarda por nombre. <see cref="Held"/> (owner, 2026-11-14) es un envío
/// retenido por la conexión: el worker nunca lo reclama y sólo lo sueltan los dos endpoints de sincronización.</summary>
public enum ShopifyOrderPushStatus
{
    Pending,
    Sent,
    Failed,
    Held,
}

/// <summary>
/// Spec 2026-11-14 §5 (<c>shopify.order_pushes</c>): una corrección de QEP que el worker manda a Shopify con <c>orderUpdate</c>. Sólo
/// lleva <b>qué grupos</b> enviar, no los valores: el worker los lee del pedido y de la fila del espejo al mandar, así que un envío
/// siempre manda lo último. Un pedido tiene a lo sumo un envío abierto, <c>Pending</c> o <c>Held</c> (índice único parcial
/// <c>IX_order_pushes_mirror_pending</c>); encolar otro grupo mientras espera lo suma al mismo (<see cref="Merge"/>). Los intentos y
/// el lease los escribe la cola del worker por SQL, sin subir la versión.
/// <para>Owner, 2026-11-14: una corrección no se pierde por la conexión. Un envío retenido (<see cref="Hold"/>) guarda el motivo en
/// <see cref="FailureCode"/> (<c>connection_inactive</c>, <c>connection_missing</c> o <c>access_denied</c>) y espera a que alguien lo
/// suelte (<see cref="Release"/>) con la conexión activa.</para>
/// </summary>
public sealed class ShopifyOrderPush
{
    public const int FailureCodeMaxLength = 64;

    private ShopifyOrderPush()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConnectionId { get; private set; }

    /// <summary>La fila de <c>shopify.orders</c>; borrarla borra sus envíos (<c>ON DELETE CASCADE</c>).</summary>
    public Guid MirrorOrderId { get; private set; }

    /// <summary>El pedido de QEP: referencia blanda a <c>quotations.orders</c>.</summary>
    public Guid QepOrderId { get; private set; }

    /// <summary>Sin repetidos y en el orden de <see cref="ShopifyPushGroup"/>.</summary>
    public IReadOnlyList<ShopifyPushGroup> Groups { get; private set; } = [];

    public ShopifyOrderPushStatus Status { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset? LeaseUntil { get; private set; }

    public string? FailureCode { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    public long Version { get; private set; }

    /// <summary>Todavía puede salir: <c>Pending</c> o <c>Held</c>. Encolar se fusiona con él.</summary>
    public bool IsOpen => Status is ShopifyOrderPushStatus.Pending or ShopifyOrderPushStatus.Held;

    public static ShopifyOrderPush Create(ShopifyOrderMirror row, IReadOnlyCollection<ShopifyPushGroup> groups, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(row);
        var qepOrderId = row.QepOrderId ?? throw new InvalidOperationException("Only a Shopify order that is in QEP can be pushed.");
        return new ShopifyOrderPush
        {
            Id = Guid.CreateVersion7(),
            TenantId = row.TenantId,
            ConnectionId = row.ConnectionId,
            MirrorOrderId = row.Id,
            QepOrderId = qepOrderId,
            Groups = Normalize(groups),
            Status = ShopifyOrderPushStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };
    }

    /// <summary>Suma los grupos a los que ya esperan. Sólo sobre uno abierto (<c>Pending</c> o <c>Held</c>, que sigue retenido): uno
    /// enviado o fallido ya no vuelve a salir. Sube la versión <b>siempre</b>, también con los mismos grupos (review Important 1): el
    /// valor cambió aunque el grupo no, y un worker que ya leyó el valor viejo y está llamando a Shopify tiene que chocar al marcarlo
    /// <c>Sent</c> y volver a mandarlo.</summary>
    public void Merge(IReadOnlyCollection<ShopifyPushGroup> more, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException($"Only an open Shopify push takes more groups; this one is {Status}.");
        }

        Groups = Normalize([.. Groups, .. Normalize(more)]);
        UpdatedAt = now;
        Version++;
    }

    /// <summary>Lo retiene con <paramref name="reason"/> (en <see cref="FailureCode"/>): el worker no lo reclama hasta que alguien lo
    /// suelte. Desde <c>Pending</c> o desde uno ya retenido (cambia el motivo).</summary>
    public void Hold(string reason, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (!IsOpen)
        {
            throw new InvalidOperationException($"Only an open Shopify push can be held; this one is {Status}.");
        }

        Status = ShopifyOrderPushStatus.Held;
        FailureCode = Code(reason);
        UpdatedAt = now;
        LeaseUntil = null;
        Version++;
    }

    /// <summary>Lo suelta para el worker: <c>Pending</c> desde cero (sin intentos, sin lease, sin motivo), así sale en la próxima
    /// pasada con la escalera de esperas completa. Sólo desde <c>Held</c>.</summary>
    public void Release(DateTimeOffset now)
    {
        if (Status != ShopifyOrderPushStatus.Held)
        {
            throw new InvalidOperationException($"Only a held Shopify push is released; this one is {Status}.");
        }

        Status = ShopifyOrderPushStatus.Pending;
        FailureCode = null;
        Attempts = 0;
        LeaseUntil = null;
        UpdatedAt = now;
        Version++;
    }

    public void MarkSent(DateTimeOffset now)
    {
        Status = ShopifyOrderPushStatus.Sent;
        SentAt = now;
        UpdatedAt = now;
        LeaseUntil = null;
        FailureCode = null;
        Version++;
    }

    /// <summary>Definitivo: <c>user_error</c>, <c>order_not_found</c>, <c>rejected</c> o <c>retries_exhausted</c> (los motivos de la
    /// conexión retienen, <see cref="Hold"/>). Se recorta al ancho de la columna.</summary>
    public void MarkFailed(string code, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Status = ShopifyOrderPushStatus.Failed;
        FailureCode = Code(code);
        UpdatedAt = now;
        LeaseUntil = null;
        Version++;
    }

    private static string Code(string code)
    {
        var trimmed = code.Trim();
        return trimmed.Length <= FailureCodeMaxLength ? trimmed : trimmed[..FailureCodeMaxLength];
    }

    private static List<ShopifyPushGroup> Normalize(IReadOnlyCollection<ShopifyPushGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (groups.Count == 0 || groups.Any(group => !Enum.IsDefined(group)))
        {
            throw new ArgumentException("A Shopify push needs at least one known group.", nameof(groups));
        }

        return [.. groups.Distinct().Order()];
    }
}
