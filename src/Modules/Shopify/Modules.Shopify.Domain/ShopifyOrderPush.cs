namespace Modules.Shopify.Domain;

/// <summary>Spec 2026-11-14 §5: lo que viaja a Shopify. Se guarda por nombre; el orden es el del cuerpo de <c>orderUpdate</c>.</summary>
public enum ShopifyPushGroup
{
    ShippingAddress,
    Note,
    Attributes,
}

/// <summary>Spec 2026-11-14 §5: el estado de un envío. Se guarda por nombre.</summary>
public enum ShopifyOrderPushStatus
{
    Pending,
    Sent,
    Failed,
}

/// <summary>
/// Spec 2026-11-14 §5 (<c>shopify.order_pushes</c>): una corrección de QEP que el worker manda a Shopify con <c>orderUpdate</c>. Sólo
/// lleva <b>qué grupos</b> enviar, no los valores: el worker los lee del pedido y de la fila del espejo al mandar, así que un envío
/// siempre manda lo último. Un pedido tiene a lo sumo un envío <c>Pending</c> (índice único parcial
/// <c>IX_order_pushes_mirror_pending</c>); encolar otro grupo mientras espera lo suma al mismo (<see cref="Merge"/>). Los intentos y
/// el lease los escribe la cola del worker por SQL, sin subir la versión.
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

    /// <summary>Suma los grupos a los que ya esperan. Sólo sobre uno pendiente: uno enviado o fallido ya no vuelve a salir.</summary>
    public void Merge(IReadOnlyCollection<ShopifyPushGroup> more, DateTimeOffset now)
    {
        if (Status != ShopifyOrderPushStatus.Pending)
        {
            throw new InvalidOperationException($"Only a pending Shopify push takes more groups; this one is {Status}.");
        }

        var merged = Normalize([.. Groups, .. Normalize(more)]);
        if (merged.SequenceEqual(Groups))
        {
            return;
        }

        Groups = merged;
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

    /// <summary>Definitivo: <c>access_denied</c>, <c>user_error</c>, <c>order_not_found</c>, <c>connection_missing</c>,
    /// <c>rejected</c> o <c>retries_exhausted</c>. Se recorta al ancho de la columna.</summary>
    public void MarkFailed(string code, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var trimmed = code.Trim();
        Status = ShopifyOrderPushStatus.Failed;
        FailureCode = trimmed.Length <= FailureCodeMaxLength ? trimmed : trimmed[..FailureCodeMaxLength];
        UpdatedAt = now;
        LeaseUntil = null;
        Version++;
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
