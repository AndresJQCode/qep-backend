namespace Modules.Siigo.Domain;

/// <summary>
/// Spec 2026-11-18 §5.6: una factura que QEP le pidió a Siigo, una fila por factura (<see cref="Kind"/>: la del pedido o, con
/// <c>freight_separate_invoice</c>, también la del flete). Guarda la petición entera (<see cref="RequestJson"/>) y su
/// <see cref="IdempotencyKey"/>: un reintento reenvía <b>los mismos bytes</b> con la misma clave, sin rearmar ni validar de nuevo, así
/// Siigo devuelve la que ya creó. <see cref="RequestJson"/> lleva datos del cliente: sólo lo leen los handlers del módulo, nunca sale en
/// un <c>GET</c> ni en un log, y el <c>ToString</c> no lo escribe. Una fila por pedido y tipo está «vigente» salvo que esté
/// <c>Rejected</c> o <c>Failed</c> (índice único parcial <c>IX_invoices_tenant_order_kind</c>).
/// </summary>
public sealed class SiigoInvoice
{
    public const int KeyMaxLength = 30;
    public const int SiigoIdMaxLength = 100;
    public const int NumberMaxLength = 50;
    public const int CufeMaxLength = 200;
    public const int ErrorCodeMaxLength = 100;
    public const int ErrorDetailMaxLength = 1000;

    /// <summary>Una <c>Pending</c> de más de esto no se reenvía sola: la documentación no dice cuánto recuerda Siigo la clave y pasado ese
    /// tiempo reenviar puede duplicar. Pasa a <see cref="SiigoInvoiceStatus.Unknown"/> y la resuelve una persona.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    /// <summary>Cuánto dura el reclamo de un envío en curso: el timeout del cliente HTTP (30 s) más margen. Dentro de la ventana, otro
    /// «Generar factura» del mismo pedido no vuelve a mandar la petición que ya va en camino; si el proceso murió, pasada la ventana se
    /// retoma.</summary>
    public static readonly TimeSpan SendLease = TimeSpan.FromMinutes(2);

    private SiigoInvoice()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConnectionId { get; private set; }

    public Guid OrderId { get; private set; }

    public SiigoInvoiceKind Kind { get; private set; }

    public SiigoInvoiceStatus Status { get; private set; }

    public string IdempotencyKey { get; private set; } = string.Empty;

    /// <summary>El cuerpo de <c>POST /v1/invoices</c>, tal como se mandó la primera vez. Nunca sale de la capa de aplicación.</summary>
    public string RequestJson { get; private set; } = string.Empty;

    public string? SiigoInvoiceId { get; private set; }

    /// <summary>El número que Siigo le dio (<c>FV-1-123</c>).</summary>
    public string? Number { get; private set; }

    public string? Cufe { get; private set; }

    /// <summary>El total que devolvió Siigo al crearla.</summary>
    public decimal? SiigoTotal { get; private set; }

    /// <summary>En un rechazo por pagos que no suman, el total que Siigo esperaba (se muestra junto al de QEP; nunca se usa para ajustar
    /// los pagos, spec §6.4).</summary>
    public decimal? ExpectedPaymentsTotal { get; private set; }

    public string? ErrorCode { get; private set; }

    public string? ErrorDetail { get; private set; }

    public int Attempts { get; private set; }

    /// <summary>La versión del pedido al armar la petición.</summary>
    public long OrderVersion { get; private set; }

    /// <summary>El pedido ya no estaba en <see cref="OrderVersion"/> cuando Siigo la creó: la pantalla lo avisa.</summary>
    public bool OrderChanged { get; private set; }

    /// <summary>Desde cuándo un «Generar factura» la está mandando. <c>null</c> si nadie la está mandando.</summary>
    public DateTimeOffset? SendingSince { get; private set; }

    /// <summary>Id de membresía de quien la pidió, como <c>InvoicedBy</c> del pedido.</summary>
    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Vigente: cuenta para el pedido (todo menos <c>Rejected</c> y <c>Failed</c>).</summary>
    public bool IsLive => Status is not (SiigoInvoiceStatus.Rejected or SiigoInvoiceStatus.Failed);

    /// <summary>Saliendo o sin resolver (<c>Pending</c> o <c>Unknown</c>): la marca manual del pedido espera.</summary>
    public bool IsOpen => Status is SiigoInvoiceStatus.Pending or SiigoInvoiceStatus.Unknown;

    /// <summary>Se puede mandar a la DIAN: existe en Siigo (con su id) y no está ya estampada.</summary>
    public bool CanStamp => Status is SiigoInvoiceStatus.Created or SiigoInvoiceStatus.StampRejected && SiigoInvoiceId is not null;

    /// <summary>Tiene PDF en Siigo: existe allá con su id (una <c>Created</c>, <c>Stamped</c> o <c>StampRejected</c>). Una resuelta a mano
    /// no tiene id, así que no se puede pedir.</summary>
    public bool HasPdf =>
        SiigoInvoiceId is not null && Status is (SiigoInvoiceStatus.Created or SiigoInvoiceStatus.Stamped or SiigoInvoiceStatus.StampRejected);

    public static SiigoInvoice PendingFor(
        Guid id,
        Guid tenantId,
        Guid connectionId,
        Guid orderId,
        SiigoInvoiceKind kind,
        string requestJson,
        long orderVersion,
        Guid createdBy,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestJson);
        return new SiigoInvoice
        {
            Id = id,
            TenantId = tenantId,
            ConnectionId = connectionId,
            OrderId = orderId,
            Kind = kind,
            Status = SiigoInvoiceStatus.Pending,
            IdempotencyKey = SiigoIdempotencyKey.For(id),
            RequestJson = requestJson,
            OrderVersion = orderVersion,
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Se está por mandar la petición: cuenta el intento y reclama la fila durante <see cref="SendLease"/>.</summary>
    public void BeginSend(DateTimeOffset now)
    {
        Attempts++;
        SendingSince = now;
        UpdatedAt = now;
    }

    public bool IsBeingSent(DateTimeOffset now) => SendingSince is { } since && now - since < SendLease;

    /// <summary>Timeout, 5xx, 429 o red: no se sabe si Siigo la creó. Sigue <c>Pending</c> y se suelta el reclamo para que el reintento
    /// pueda mandar los mismos bytes con la misma clave.</summary>
    /// <param name="errorCode">Por qué falló este intento (<c>siigo.invoice.*</c>), para que el pedido se lo diga a quien factura. Nulo
    /// conserva el que ya había: es la factura de flete que espera a la del pedido, que no falló por sí misma.</param>
    public void MarkTransient(string? errorCode, DateTimeOffset now)
    {
        if (errorCode is not null)
        {
            ErrorCode = Cap(errorCode, ErrorCodeMaxLength);
            ErrorDetail = null;
        }

        SendingSince = null;
        UpdatedAt = now;
    }

    /// <summary>Siigo la creó. Con CUFE ya salió a la DIAN (<c>Stamped</c>); sin él, <c>Created</c>.</summary>
    public void MarkCreated(string siigoInvoiceId, string number, decimal total, string? cufe, DateTimeOffset now)
    {
        SiigoInvoiceId = Cap(siigoInvoiceId, SiigoIdMaxLength);
        Number = Cap(number, NumberMaxLength);
        SiigoTotal = total;
        Cufe = string.IsNullOrWhiteSpace(cufe) ? null : Cap(cufe, CufeMaxLength);
        Status = Cufe is null ? SiigoInvoiceStatus.Created : SiigoInvoiceStatus.Stamped;
        ClearError();
        SendingSince = null;
        UpdatedAt = now;
    }

    /// <summary>Siigo respondió un error de validación: no la creó y el pedido queda sin marcar. El detalle es de Siigo, recortado y sin
    /// datos del cliente.</summary>
    public void MarkRejected(string errorCode, string errorDetail, decimal? expectedPaymentsTotal, DateTimeOffset now)
    {
        Status = SiigoInvoiceStatus.Rejected;
        ErrorCode = Cap(errorCode, ErrorCodeMaxLength);
        ErrorDetail = Cap(errorDetail, ErrorDetailMaxLength);
        ExpectedPaymentsTotal = expectedPaymentsTotal;
        SendingSince = null;
        UpdatedAt = now;
    }

    /// <summary>Se descarta una fila que nunca se mandó (la del flete cuando la del pedido fue rechazada).</summary>
    public void MarkFailed(string errorCode, string errorDetail, DateTimeOffset now)
    {
        Status = SiigoInvoiceStatus.Failed;
        ErrorCode = Cap(errorCode, ErrorCodeMaxLength);
        ErrorDetail = Cap(errorDetail, ErrorDetailMaxLength);
        SendingSince = null;
        UpdatedAt = now;
    }

    /// <summary>La DIAN la aceptó (o ya la había aceptado). Una <c>StampRejected</c> que se corrigió en Siigo y se volvió a estampar
    /// también pasa por acá.</summary>
    public void MarkStamped(string? cufe, DateTimeOffset now)
    {
        EnsureCanStamp();
        Cufe = string.IsNullOrWhiteSpace(cufe) ? Cufe : Cap(cufe, CufeMaxLength);
        Status = SiigoInvoiceStatus.Stamped;
        ClearError();
        UpdatedAt = now;
    }

    public void MarkStampRejected(string errorCode, string errorDetail, DateTimeOffset now)
    {
        EnsureCanStamp();
        Status = SiigoInvoiceStatus.StampRejected;
        ErrorCode = Cap(errorCode, ErrorCodeMaxLength);
        ErrorDetail = Cap(errorDetail, ErrorDetailMaxLength);
        UpdatedAt = now;
    }

    /// <summary>La transición perezosa de §5.4: una <c>Pending</c> de más de <see cref="StaleAfter"/> pasa a <c>Unknown</c>, sin worker,
    /// al leerla o reintentarla. Devuelve si cambió.</summary>
    public bool ExpireIfStale(DateTimeOffset now)
    {
        if (Status != SiigoInvoiceStatus.Pending || now - CreatedAt <= StaleAfter)
        {
            return false;
        }

        Status = SiigoInvoiceStatus.Unknown;
        SendingSince = null;
        UpdatedAt = now;
        return true;
    }

    /// <summary>La persona miró en Siigo y la factura existe con ese número: queda <c>Created</c> (sin id de Siigo: no se puede estampar
    /// desde QEP).</summary>
    public void ResolveExists(string number, DateTimeOffset now)
    {
        EnsureUnknown();
        if (string.IsNullOrWhiteSpace(number))
        {
            throw new SiigoDomainException(SiigoErrorCodes.InvoiceStateInvalid, "The number of the invoice that exists in Siigo is required.");
        }

        Number = Cap(number.Trim(), NumberMaxLength);
        Status = SiigoInvoiceStatus.Created;
        ClearError();
        UpdatedAt = now;
    }

    /// <summary>La persona miró en Siigo y no existe: <c>Failed</c>, el pedido queda libre para facturar de nuevo.</summary>
    public void ResolveMissing(DateTimeOffset now)
    {
        EnsureUnknown();
        Status = SiigoInvoiceStatus.Failed;
        UpdatedAt = now;
    }

    public void MarkOrderChanged() => OrderChanged = true;

    // Un record imprimiría la petición (con el documento y la dirección del cliente) en cualquier log; esta clase no es un record, pero
    // el ToString por defecto sólo escribe el tipo: se deja explícito para que nadie lo «mejore».
    public override string ToString() => $"SiigoInvoice {{ Id = {Id}, Kind = {Kind}, Status = {Status} }}";

    private void EnsureUnknown()
    {
        if (Status != SiigoInvoiceStatus.Unknown)
        {
            throw new SiigoDomainException(SiigoErrorCodes.InvoiceStateInvalid, "Only an invoice in an unknown state can be resolved.");
        }
    }

    private void EnsureCanStamp()
    {
        if (!CanStamp)
        {
            throw new SiigoDomainException(
                SiigoErrorCodes.InvoiceStateInvalid, "Only an invoice created in Siigo, not yet sent to the DIAN, can be stamped.");
        }
    }

    private void ClearError()
    {
        ErrorCode = null;
        ErrorDetail = null;
        ExpectedPaymentsTotal = null;
    }

    private static string Cap(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
