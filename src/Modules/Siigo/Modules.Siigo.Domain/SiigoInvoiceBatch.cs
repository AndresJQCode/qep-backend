namespace Modules.Siigo.Domain;

/// <summary>
/// Una tanda de facturación con Siigo: los pedidos que una persona marcó en <c>/orders</c> y mandó a facturar juntos. Un worker los
/// procesa uno por uno, y cada pedido es independiente: uno que falla no frena ni revierte a los demás (una factura emitida no se deshace).
/// <see cref="RequestedBy"/> es el usuario (sujeto) que la pidió: el worker corre como él, con los permisos que tenga al procesar cada
/// pedido. Una tanda con ítems <c>Invoiced</c> no se purga: guarda qué factura salió por qué pedido.
/// </summary>
public sealed class SiigoInvoiceBatch
{
    public const int MaxItems = 50;

    private readonly List<SiigoInvoiceBatchItem> _items = [];

    private SiigoInvoiceBatch()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>El usuario que la pidió (<c>IExecutionContext.SubjectId</c>).</summary>
    public Guid RequestedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public IReadOnlyList<SiigoInvoiceBatchItem> Items => _items;

    public bool IsFinished => FinishedAt is not null;

    public static SiigoInvoiceBatch Create(Guid id, Guid tenantId, Guid requestedBy, IReadOnlyList<Guid> orderIds, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(orderIds);
        if (orderIds.Count is 0 or > MaxItems)
        {
            throw new SiigoDomainException(SiigoErrorCodes.InvoiceBatchSize, $"A batch holds between 1 and {MaxItems} orders.");
        }

        if (orderIds.Any(orderId => orderId == Guid.Empty) || orderIds.Distinct().Count() != orderIds.Count)
        {
            throw new SiigoDomainException(SiigoErrorCodes.InvoiceBatchOrders, "The orders of a batch are required and cannot repeat.");
        }

        var batch = new SiigoInvoiceBatch { Id = id, TenantId = tenantId, RequestedBy = requestedBy, CreatedAt = now };
        for (var position = 0; position < orderIds.Count; position++)
        {
            batch._items.Add(SiigoInvoiceBatchItem.Pending(Guid.CreateVersion7(), id, tenantId, orderIds[position], position, now));
        }

        return batch;
    }

    /// <summary>Cierra la tanda cuando ya no queda ningún pedido por procesar. Devuelve si lo hizo ahora; cerrada una vez, no cambia.</summary>
    public bool CloseIfFinished(DateTimeOffset now)
    {
        if (FinishedAt is not null || _items.Any(item => !item.IsSettled))
        {
            return false;
        }

        FinishedAt = now;
        return true;
    }

    public override string ToString() => $"SiigoInvoiceBatch {{ Id = {Id}, Items = {_items.Count} }}";
}

/// <summary>Un pedido de una tanda. Los códigos de <see cref="ErrorCode"/> y <see cref="MissingCodes"/> son los del contrato
/// (<c>siigo.*</c> y los de <c>SiigoMissing</c>), nunca el texto de Siigo ni datos del cliente.</summary>
public sealed class SiigoInvoiceBatchItem
{
    /// <summary>Cuántas veces se manda un pedido: una (owner, 2026-11-24). No se programan reintentos para después: con Siigo caído la
    /// factura se puede crear directamente allá, y un envío posterior la duplicaría.</summary>
    public const int MaxAttempts = 1;

    public const int ErrorCodeMaxLength = 100;
    public const int MissingCodesMaxLength = 500;
    public const int InvoiceNumberMaxLength = 50;

    private SiigoInvoiceBatchItem()
    {
    }

    public Guid Id { get; private set; }

    public Guid BatchId { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid OrderId { get; private set; }

    public int Position { get; private set; }

    public SiigoInvoiceBatchItemStatus Status { get; private set; }

    public string? ErrorCode { get; private set; }

    /// <summary>Lo que le faltaba a un pedido saltado por <c>not_ready</c>; en la columna va separado por comas.</summary>
    public IReadOnlyList<string> MissingCodes { get; private set; } = [];

    public string? InvoiceNumber { get; private set; }

    public int Attempts { get; private set; }

    /// <summary>No antes de esta hora se reclama.</summary>
    public DateTimeOffset AvailableAt { get; private set; }

    /// <summary>Hasta cuándo es de un worker. Vencido, el pedido vuelve a reclamarse.</summary>
    public DateTimeOffset? LeaseUntil { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsSettled => Status is SiigoInvoiceBatchItemStatus.Invoiced or SiigoInvoiceBatchItemStatus.Skipped or SiigoInvoiceBatchItemStatus.Failed;

    internal static SiigoInvoiceBatchItem Pending(Guid id, Guid batchId, Guid tenantId, Guid orderId, int position, DateTimeOffset now) =>
        new()
        {
            Id = id,
            BatchId = batchId,
            TenantId = tenantId,
            OrderId = orderId,
            Position = position,
            Status = SiigoInvoiceBatchItemStatus.Pending,
            AvailableAt = now,
            UpdatedAt = now,
        };

    /// <summary>El worker lo reclama: cuenta el intento y lo retiene hasta <paramref name="lease"/> después de <paramref name="now"/>.</summary>
    public void BeginSend(DateTimeOffset now, TimeSpan lease)
    {
        Status = SiigoInvoiceBatchItemStatus.Sending;
        Attempts++;
        LeaseUntil = now + lease;
        UpdatedAt = now;
    }

    public void MarkInvoiced(string number, DateTimeOffset now)
    {
        if (IsSettled)
        {
            return;
        }

        Status = SiigoInvoiceBatchItemStatus.Invoiced;
        InvoiceNumber = Cap(number, InvoiceNumberMaxLength);
        Settle(null, [], now);
    }

    /// <summary>El pedido no estaba en condiciones (no listo, ya facturado, anulado…): no se llamó a Siigo.</summary>
    public void MarkSkipped(string errorCode, IReadOnlyList<string> missingCodes, DateTimeOffset now)
    {
        if (IsSettled)
        {
            return;
        }

        Status = SiigoInvoiceBatchItemStatus.Skipped;
        Settle(errorCode, missingCodes, now);
    }

    public void MarkFailed(string errorCode, DateTimeOffset now)
    {
        if (IsSettled)
        {
            return;
        }

        Status = SiigoInvoiceBatchItemStatus.Failed;
        Settle(errorCode, [], now);
    }

    private void Settle(string? errorCode, IReadOnlyList<string> missingCodes, DateTimeOffset now)
    {
        ErrorCode = errorCode is null ? null : Cap(errorCode, ErrorCodeMaxLength);
        MissingCodes = missingCodes;
        LeaseUntil = null;
        UpdatedAt = now;
    }

    private static string Cap(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
