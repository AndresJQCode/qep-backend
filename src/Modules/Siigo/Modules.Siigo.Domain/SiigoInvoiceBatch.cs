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

    /// <summary>Cuándo terminó, para quien la lee: <see cref="FinishedAt"/> o, si quedó sin cerrar con todos sus pedidos resueltos, la hora
    /// del último que se resolvió; <c>null</c> si todavía falta alguno. Pasa cuando dos réplicas resuelven a la vez los dos últimos
    /// pedidos: cada una ve el del otro sin terminar, ninguna cierra la tanda y ya no queda quien vuelva a intentarlo. No escribe
    /// nada.</summary>
    public DateTimeOffset? ReadFinishedAt() =>
        FinishedAt ?? (_items.Count > 0 && _items.All(item => item.IsSettled) ? _items.Max(item => item.UpdatedAt) : (DateTimeOffset?)null);

    /// <param name="paymentsJson">Las formas de pago que la persona eligió, sólo en una tanda de un pedido: son por comprobante de
    /// ese pedido (el validador lo frena antes con el campo).</param>
    public static SiigoInvoiceBatch Create(
        Guid id, Guid tenantId, Guid requestedBy, IReadOnlyList<Guid> orderIds, DateTimeOffset now, string? paymentsJson = null)
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

        if (paymentsJson is not null && orderIds.Count != 1)
        {
            throw new ArgumentException("Payment choices only travel with a batch of a single order.", nameof(paymentsJson));
        }

        var batch = new SiigoInvoiceBatch { Id = id, TenantId = tenantId, RequestedBy = requestedBy, CreatedAt = now };
        for (var position = 0; position < orderIds.Count; position++)
        {
            batch._items.Add(
                SiigoInvoiceBatchItem.Pending(Guid.CreateVersion7(), id, tenantId, orderIds[position], position, paymentsJson, now));
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

    /// <summary>La fila del pedido en <c>siigo.invoices</c> que produjo el intento, si hubo una.</summary>
    public Guid? InvoiceId { get; private set; }

    /// <summary>Las formas de pago elegidas al pedir la factura de un solo pedido; <c>null</c> usa la propuesta de la cuenta de
    /// cobro.</summary>
    public string? PaymentsJson { get; private set; }

    /// <summary>La factura existe pero el pedido no se pudo marcar (anulado o marcado a mano en medio).</summary>
    public bool OrderMarkFailed { get; private set; }

    public int Attempts { get; private set; }

    /// <summary>No antes de esta hora se reclama.</summary>
    public DateTimeOffset AvailableAt { get; private set; }

    /// <summary>Hasta cuándo es de un worker. Vencido, el pedido vuelve a reclamarse.</summary>
    public DateTimeOffset? LeaseUntil { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>El worker ya no tiene nada que hacer con el pedido. Una por confirmar cierra: depende de una persona.</summary>
    public bool IsSettled =>
        Status is SiigoInvoiceBatchItemStatus.Invoiced or SiigoInvoiceBatchItemStatus.Skipped or SiigoInvoiceBatchItemStatus.Failed
            or SiigoInvoiceBatchItemStatus.ToConfirm;

    internal static SiigoInvoiceBatchItem Pending(
        Guid id, Guid batchId, Guid tenantId, Guid orderId, int position, string? paymentsJson, DateTimeOffset now) =>
        new()
        {
            Id = id,
            BatchId = batchId,
            TenantId = tenantId,
            OrderId = orderId,
            Position = position,
            PaymentsJson = paymentsJson,
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

    public void MarkInvoiced(string number, DateTimeOffset now, Guid? invoiceId = null, bool orderMarkFailed = false)
    {
        if (IsSettled)
        {
            return;
        }

        Status = SiigoInvoiceBatchItemStatus.Invoiced;
        InvoiceNumber = Cap(number, InvoiceNumberMaxLength);
        InvoiceId = invoiceId;
        OrderMarkFailed = orderMarkFailed;
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

    public void MarkFailed(string errorCode, DateTimeOffset now, Guid? invoiceId = null)
    {
        if (IsSettled)
        {
            return;
        }

        Status = SiigoInvoiceBatchItemStatus.Failed;
        InvoiceId = invoiceId;
        Settle(errorCode, [], now);
    }

    /// <summary>Se mandó y no se sabe si Siigo creó la factura: el pedido queda enlazado a su fila.</summary>
    public void MarkToConfirm(Guid invoiceId, string? errorCode, DateTimeOffset now)
    {
        if (IsSettled)
        {
            return;
        }

        Status = SiigoInvoiceBatchItemStatus.ToConfirm;
        InvoiceId = invoiceId;
        Settle(errorCode, [], now);
    }

    /// <summary>El estado que se devuelve al leer (spec 2026-11-24 §2). Un pedido guardado <c>ToConfirm</c> sigue a su factura: así
    /// un reenvío desde otra tanda, o una resolución desde el pedido, se ve sin escribir en ítems ajenos. Mientras siga por confirmar, el
    /// código es el del ítem si la fila no cambió después de que se cerró (dice por qué no salió un reenvío que no tocó la fila) y el de
    /// la fila si cambió después; si el que manda viene vacío, el otro. Uno guardado <c>Invoiced</c> sólo cambia por la de flete o por la
    /// anulación. Con la fila enlazada <c>Annulled</c> (spec 2026-11-25 §1), cualquiera de los dos se lee <c>Failed</c> con el código de
    /// la fila, sin número ni acciones, aunque el pedido se haya vuelto a facturar.</summary>
    /// <param name="invoice">La fila enlazada (<see cref="InvoiceId"/>); otra, o ninguna, deja lo guardado.</param>
    /// <param name="freight">La de flete de esa factura del pedido, si tiene (la más nueva). Con la del pedido ya en Siigo, el ítem
    /// —guardado <c>ToConfirm</c> o <c>Invoiced</c>— sólo se lee facturado si la de flete también existe allá (decidido el
    /// 2026-11-25): <c>Sending</c> mientras se manda; <c>Failed</c> con <c>siigo.invoice.freight_pending</c> si está <c>Pending</c> y
    /// con <c>siigo.invoice.freight_not_created</c> si quedó <c>Rejected</c>, <c>Failed</c> o <c>Annulled</c>; <c>ToConfirm</c> con
    /// <c>siigo.invoice.freight_unconfirmed</c>, sin nada que ofrecer, si quedó <c>Unknown</c>. Una lectura que no la pase la daría por
    /// facturada.</param>
    public SiigoBatchItemState Effective(SiigoInvoice? invoice, DateTimeOffset now, SiigoInvoice? freight = null)
    {
        var stored = new SiigoBatchItemState(Status, ErrorCode, InvoiceNumber, false, false, null);
        if (invoice is null || invoice.Id != InvoiceId)
        {
            return stored;
        }

        // El pedido ya no está facturado por esta tanda y la fila dice por qué. Va antes de lo demás: si no, uno que se leía fallido
        // por la de flete volvería a leerse con lo que guardó.
        if (invoice.Status == SiigoInvoiceStatus.Annulled
            && Status is SiigoInvoiceBatchItemStatus.Invoiced or SiigoInvoiceBatchItemStatus.ToConfirm)
        {
            return new SiigoBatchItemState(SiigoInvoiceBatchItemStatus.Failed, invoice.ErrorCode, null, false, false, null);
        }

        if (Status == SiigoInvoiceBatchItemStatus.Invoiced)
        {
            return invoice.Status is SiigoInvoiceStatus.Created or SiigoInvoiceStatus.Stamped or SiigoInvoiceStatus.StampRejected
                ? WithItsFreight(stored, freight, now)
                : stored;
        }

        if (Status != SiigoInvoiceBatchItemStatus.ToConfirm)
        {
            return stored;
        }

        return invoice.Status switch
        {
            SiigoInvoiceStatus.Pending when invoice.IsBeingSent(now) =>
                new SiigoBatchItemState(SiigoInvoiceBatchItemStatus.Sending, null, null, false, false, null),
            SiigoInvoiceStatus.Pending or SiigoInvoiceStatus.Unknown => new SiigoBatchItemState(
                SiigoInvoiceBatchItemStatus.ToConfirm,
                // UpdatedAt del ítem es la hora en que se cerró: cerrado, nada lo vuelve a escribir. La fila se guarda antes.
                invoice.UpdatedAt > UpdatedAt ? invoice.ErrorCode ?? ErrorCode : ErrorCode ?? invoice.ErrorCode,
                null,
                invoice.CanResend(now),
                // Lo mismo que ofrece la pantalla del pedido: una que nunca salió no se ofrece resolver.
                invoice.OffersResolution(now),
                invoice.OffersResolution(now) ? invoice.MissingAvailableAt(now) : null),
            SiigoInvoiceStatus.Created or SiigoInvoiceStatus.Stamped or SiigoInvoiceStatus.StampRejected =>
                WithItsFreight(
                    new SiigoBatchItemState(SiigoInvoiceBatchItemStatus.Invoiced, null, invoice.Number, false, false, null), freight, now),
            SiigoInvoiceStatus.Failed => new SiigoBatchItemState(
                SiigoInvoiceBatchItemStatus.Failed, SiigoErrorCodes.InvoiceResolvedMissing, null, false, false, null),
            SiigoInvoiceStatus.Rejected =>
                new SiigoBatchItemState(SiigoInvoiceBatchItemStatus.Failed, invoice.ErrorCode, null, false, false, null),
            _ => throw new InvalidOperationException($"Unknown invoice status '{invoice.Status}'."),
        };
    }

    // Lo que se lee de un pedido cuya factura existe: facturado sólo si su de flete, cuando la tiene, también.
    private static SiigoBatchItemState WithItsFreight(SiigoBatchItemState invoiced, SiigoInvoice? freight, DateTimeOffset now) =>
        freight is not { Kind: SiigoInvoiceKind.Freight }
            ? invoiced
            : freight.Status switch
            {
                SiigoInvoiceStatus.Pending when freight.IsBeingSent(now) =>
                    new SiigoBatchItemState(SiigoInvoiceBatchItemStatus.Sending, null, null, false, false, null),
                SiigoInvoiceStatus.Pending => OfFreight(SiigoInvoiceBatchItemStatus.Failed, SiigoErrorCodes.InvoiceFreightPending),
                SiigoInvoiceStatus.Unknown =>
                    OfFreight(SiigoInvoiceBatchItemStatus.ToConfirm, SiigoErrorCodes.InvoiceFreightUnconfirmed),
                // Una Annulled tampoco está en Siigo. La anulación suelta todas las del pedido a la vez, así que no se espera aquí con la
                // del pedido viva.
                SiigoInvoiceStatus.Rejected or SiigoInvoiceStatus.Failed or SiigoInvoiceStatus.Annulled =>
                    OfFreight(SiigoInvoiceBatchItemStatus.Failed, SiigoErrorCodes.InvoiceFreightNotCreated),
                _ => invoiced,
            };

    // Sin acciones: la de flete se reintenta o se resuelve desde el pedido; las de la tanda actúan sobre la factura del pedido.
    private static SiigoBatchItemState OfFreight(SiigoInvoiceBatchItemStatus status, string errorCode) =>
        new(status, errorCode, null, false, false, null);

    private void Settle(string? errorCode, IReadOnlyList<string> missingCodes, DateTimeOffset now)
    {
        ErrorCode = errorCode is null ? null : Cap(errorCode, ErrorCodeMaxLength);
        MissingCodes = missingCodes;
        LeaseUntil = null;
        UpdatedAt = now;
    }

    private static string Cap(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
