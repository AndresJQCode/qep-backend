namespace Modules.Shopify.Domain;

/// <summary>Spec 2026-11-14 §4: el estado de un cambio de Shopify. Se guarda por nombre.</summary>
public enum ShopifyOrderEditStatus
{
    Pending,
    Applied,
    Blocked,
    Rejected,
    Reviewed,
    Superseded,
}

/// <summary>
/// Spec 2026-11-14 §3 (<c>shopify.order_edits</c>; el spec la llama «order_changes» y la API dice «changes», plan D1): una edición
/// de Shopify sobre un pedido ya importado, con el diff por grupo armado para pintar (<see cref="Groups"/>, JSON) y el snapshot
/// que la trajo (<see cref="Payload"/>). Un pedido tiene a lo sumo un cambio abierto (<see cref="IsOpen"/>): el siguiente snapshot
/// lo reemplaza (<see cref="Supersede"/>). La concurrencia entre cambios la da la versión de la fila del espejo, que crear o
/// reemplazar un cambio siempre toca (D10).
/// </summary>
public sealed class ShopifyOrderEdit
{
    public const int StatusReasonMaxLength = 64;

    private ShopifyOrderEdit()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConnectionId { get; private set; }

    /// <summary>La fila de <c>shopify.orders</c>; borrarla borra sus cambios (<c>ON DELETE CASCADE</c>).</summary>
    public Guid MirrorOrderId { get; private set; }

    /// <summary>El pedido de QEP: referencia blanda a <c>quotations.orders</c>.</summary>
    public Guid QepOrderId { get; private set; }

    /// <summary>El <c>updated_at</c> de la versión aplicada contra la que se calculó el diff; <c>null</c> si no se sabe.</summary>
    public DateTimeOffset? BaseUpdatedAt { get; private set; }

    /// <summary>El <c>updated_at</c> del snapshot que trajo el cambio: la misma entrega otra vez no crea otro.</summary>
    public DateTimeOffset SnapshotUpdatedAt { get; private set; }

    public string Payload { get; private set; } = string.Empty;

    public string Groups { get; private set; } = string.Empty;

    public ShopifyOrderEditStatus Status { get; private set; }

    public string? StatusReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    /// <summary>La membresía que aceptó, rechazó o revisó; <c>null</c> si lo decidió el sistema (aplicado solo o reemplazado).</summary>
    public Guid? DecidedBy { get; private set; }

    public long Version { get; private set; }

    public bool IsOpen => Status is ShopifyOrderEditStatus.Pending or ShopifyOrderEditStatus.Blocked;

    public static ShopifyOrderEdit Create(
        Guid tenantId,
        Guid connectionId,
        Guid mirrorOrderId,
        Guid qepOrderId,
        DateTimeOffset? baseUpdatedAt,
        DateTimeOffset snapshotUpdatedAt,
        string payloadJson,
        string groupsJson,
        ShopifyOrderEditStatus status,
        string? reason,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(tenantId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(connectionId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(mirrorOrderId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(qepOrderId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupsJson);
        if (status is not (ShopifyOrderEditStatus.Pending or ShopifyOrderEditStatus.Blocked or ShopifyOrderEditStatus.Applied))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "A Shopify change starts pending, blocked or applied.");
        }

        if (status == ShopifyOrderEditStatus.Blocked && string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A blocked change needs its reason.", nameof(reason));
        }

        return new ShopifyOrderEdit
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            MirrorOrderId = mirrorOrderId,
            QepOrderId = qepOrderId,
            BaseUpdatedAt = baseUpdatedAt,
            SnapshotUpdatedAt = snapshotUpdatedAt,
            Payload = payloadJson,
            Groups = groupsJson,
            Status = status,
            StatusReason = Reason(reason),
            CreatedAt = now,
            DecidedAt = status == ShopifyOrderEditStatus.Applied ? now : null,
            Version = 1,
        };
    }

    /// <summary>Se aplicó (solo, con <paramref name="decidedBy"/> nulo, o porque alguien lo aceptó). <paramref name="groupsJson"/>
    /// son los grupos como quedaron al aplicar: aceptar vuelve a planear contra el pedido de ese momento.</summary>
    public void MarkApplied(string groupsJson, Guid? decidedBy, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupsJson);
        EnsureOpen();
        Status = ShopifyOrderEditStatus.Applied;
        StatusReason = null;
        Groups = groupsJson;
        DecidedAt = now;
        DecidedBy = decidedBy;
        Version++;
    }

    /// <summary>No se puede aplicar ahora (pedido no editable, pago no permitido, producto sin emparejar).</summary>
    public void Block(string reason, string groupsJson, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupsJson);
        _ = now;
        EnsureOpen();
        Status = ShopifyOrderEditStatus.Blocked;
        StatusReason = Reason(reason);
        Groups = groupsJson;
        Version++;
    }

    /// <summary>Review M-2: la aplicación automática la rechazó una regla del pedido que no es «no editable» (por ejemplo
    /// <c>order.order.store_pickup</c>). El cambio sigue <c>Pending</c>, para que una persona lo acepte o lo rechace, y
    /// <see cref="StatusReason"/> guarda el código de la regla para que la bandeja diga por qué no se aplicó solo.</summary>
    public void HoldRejected(string ruleCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleCode);
        EnsureOpen();
        if (Status != ShopifyOrderEditStatus.Pending)
        {
            throw new InvalidOperationException("Only a pending Shopify change keeps the rule that rejected its automatic apply.");
        }

        StatusReason = Reason(ruleCode);
        Version++;
    }

    public void Reject(Guid decidedBy, DateTimeOffset now) => Close(ShopifyOrderEditStatus.Rejected, decidedBy, now);

    /// <summary>Plan D9: «revisado» cierra un cambio bloqueado que el asesor ya resolvió a mano. Sólo desde <c>Blocked</c>.</summary>
    public void Review(Guid decidedBy, DateTimeOffset now)
    {
        EnsureOpen();
        if (Status != ShopifyOrderEditStatus.Blocked)
        {
            throw new ShopifyDomainException(ShopifyErrorCodes.EditNotBlocked, "Only a blocked Shopify change can be marked as reviewed.");
        }

        Close(ShopifyOrderEditStatus.Reviewed, decidedBy, now);
    }

    /// <summary>Llegó otro snapshot del mismo pedido: el diff nuevo reemplaza a éste (§3).</summary>
    public void Supersede(DateTimeOffset now) => Close(ShopifyOrderEditStatus.Superseded, null, now);

    private void Close(ShopifyOrderEditStatus status, Guid? decidedBy, DateTimeOffset now)
    {
        EnsureOpen();
        Status = status;
        DecidedAt = now;
        DecidedBy = decidedBy;
        Version++;
    }

    private void EnsureOpen()
    {
        if (!IsOpen)
        {
            throw new ShopifyDomainException(ShopifyErrorCodes.EditNotOpen, "The Shopify change is no longer pending or blocked.");
        }
    }

    private static string? Reason(string? reason)
    {
        var trimmed = reason?.Trim();
        return string.IsNullOrEmpty(trimmed)
            ? null
            : trimmed.Length <= StatusReasonMaxLength ? trimmed : trimmed[..StatusReasonMaxLength];
    }
}
