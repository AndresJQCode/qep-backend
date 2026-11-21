namespace Modules.Coordinadora.Domain;

/// <summary>
/// Spec 2026-11-21 §5.4 y §5.5: una guía que QEP le pidió a Coordinadora, una fila por intento. Guarda la petición entera
/// (<see cref="RequestJson"/>, sin credenciales) para el diagnóstico y la vista previa de lo que salió. <see cref="RequestJson"/>,
/// <see cref="ErrorDetail"/> y <see cref="LabelPdf"/> llevan datos del destinatario: sólo los leen los handlers del módulo, nunca salen en
/// el <c>GET</c> ni en un log, y el <c>ToString</c> no los escribe. Una fila por pedido está «viva» en <c>Pending</c>, <c>Created</c> o
/// <c>Unknown</c> (índice único parcial <c>IX_shipments_tenant_order_live</c>). <b>No hay reenvío</b>: Coordinadora no tiene clave de
/// idempotencia, así que una petición que no se sabe si llegó pasa a <see cref="CoordinadoraShipmentStatus.Unknown"/> y la resuelve una
/// persona.
/// </summary>
public sealed class CoordinadoraShipment
{
    public const int GuideNumberMaxLength = 30;
    public const int ErrorCodeMaxLength = 100;
    public const int ErrorDetailMaxLength = 200;
    public const int TrackingStatusMaxLength = 100;
    public const int TrackingDetailMaxLength = 500;

    /// <summary>Cuánto dura el reclamo de un envío en curso: el timeout del cliente HTTP (30 s) más margen. Dentro de la ventana, otro
    /// «Generar guía» del mismo pedido devuelve la fila tal cual; pasada la ventana, una <c>Pending</c> es una petición que murió sin
    /// respuesta y pasa a <c>Unknown</c> al leerla.</summary>
    public static readonly TimeSpan SendLease = TimeSpan.FromMinutes(2);

    private CoordinadoraShipment()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConnectionId { get; private set; }

    public Guid OrderId { get; private set; }

    public CoordinadoraShipmentStatus Status { get; private set; }

    /// <summary>El cuerpo <c>p</c> de <c>Guias_generarGuia</c> sin el bloque de credenciales, tal como se mandó. Nunca sale de la capa de
    /// aplicación.</summary>
    public string RequestJson { get; private set; } = string.Empty;

    /// <summary><c>codigo_remision</c>: la guía (11 dígitos).</summary>
    public string? GuideNumber { get; private set; }

    /// <summary>El <c>valor_declarado</c> que viajó.</summary>
    public decimal DeclaredValue { get; private set; }

    /// <summary>El PDF del rótulo si Coordinadora lo devolvió al crear la guía (V-2). Sin él, el rótulo se pide en vivo.</summary>
    public byte[]? LabelPdf { get; private set; }

    public string? TrackingStatus { get; private set; }

    public string? TrackingDetail { get; private set; }

    public DateTimeOffset? TrackedAt { get; private set; }

    public string? ErrorCode { get; private set; }

    /// <summary>Lo que dijo Coordinadora, recortado a <see cref="ErrorDetailMaxLength"/>: puede repetir datos del destinatario.</summary>
    public string? ErrorDetail { get; private set; }

    public int Attempts { get; private set; }

    /// <summary>La versión del pedido al armar la petición.</summary>
    public long OrderVersion { get; private set; }

    /// <summary>Desde cuándo un «Generar guía» la está mandando. <c>null</c> si nadie la está mandando.</summary>
    public DateTimeOffset? SendingSince { get; private set; }

    /// <summary>Id de membresía de quien la pidió.</summary>
    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public Guid? CancelledBy { get; private set; }

    /// <summary>Viva: cuenta para el pedido (<c>Pending</c>, <c>Created</c> o <c>Unknown</c>).</summary>
    public bool IsLive => Status is CoordinadoraShipmentStatus.Pending or CoordinadoraShipmentStatus.Created or CoordinadoraShipmentStatus.Unknown;

    /// <summary>Existe en Coordinadora con su número: se puede imprimir, rastrear y anular.</summary>
    public bool IsCreated => Status == CoordinadoraShipmentStatus.Created && GuideNumber is not null;

    public static CoordinadoraShipment PendingFor(
        Guid id,
        Guid tenantId,
        Guid connectionId,
        Guid orderId,
        string requestJson,
        decimal declaredValue,
        long orderVersion,
        Guid createdBy,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestJson);
        return new CoordinadoraShipment
        {
            Id = id,
            TenantId = tenantId,
            ConnectionId = connectionId,
            OrderId = orderId,
            Status = CoordinadoraShipmentStatus.Pending,
            RequestJson = requestJson,
            DeclaredValue = declaredValue,
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

    /// <summary>Coordinadora devolvió el número de guía. Con el PDF del rótulo si lo trajo.</summary>
    public void MarkCreated(string guideNumber, byte[]? labelPdf, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guideNumber);
        GuideNumber = CoordinadoraText.Cap(guideNumber, GuideNumberMaxLength);
        LabelPdf = labelPdf is { Length: > 0 } ? labelPdf : null;
        Status = CoordinadoraShipmentStatus.Created;
        ClearError();
        SendingSince = null;
        UpdatedAt = now;
    }

    /// <summary>Coordinadora la rechazó: no la creó y el pedido queda libre para armar otra. El detalle es de Coordinadora, recortado.</summary>
    public void MarkRejected(string errorCode, string? errorDetail, DateTimeOffset now)
    {
        Status = CoordinadoraShipmentStatus.Rejected;
        ErrorCode = CoordinadoraText.Cap(errorCode, ErrorCodeMaxLength) ?? "rejected";
        ErrorDetail = CoordinadoraText.Cap(errorDetail, ErrorDetailMaxLength);
        SendingSince = null;
        UpdatedAt = now;
    }

    /// <summary>Timeout, 5xx o red: no se sabe si Coordinadora la creó, y sin clave de idempotencia no se reenvía. La resuelve una persona.</summary>
    public void MarkUnknown(string reason, DateTimeOffset now)
    {
        Status = CoordinadoraShipmentStatus.Unknown;
        ErrorCode = CoordinadoraText.Cap(reason, ErrorCodeMaxLength) ?? "unknown";
        ErrorDetail = null;
        SendingSince = null;
        UpdatedAt = now;
    }

    /// <summary>La transición perezosa de §5.4: una <c>Pending</c> cuyo reclamo venció es una petición que murió sin respuesta. Pasa a
    /// <c>Unknown</c> al leerla. Devuelve si cambió.</summary>
    public bool ExpireIfStale(DateTimeOffset now)
    {
        if (Status != CoordinadoraShipmentStatus.Pending || IsBeingSent(now))
        {
            return false;
        }

        MarkUnknown("send_lease_expired", now);
        return true;
    }

    /// <summary>La persona miró en Coordinadora y la guía existe con ese número: queda <c>Created</c>, sin rótulo guardado (se pide en vivo).</summary>
    public void ResolveExists(string guideNumber, DateTimeOffset now)
    {
        EnsureUnknown();
        if (string.IsNullOrWhiteSpace(guideNumber))
        {
            throw new CoordinadoraDomainException(
                CoordinadoraErrorCodes.ShipmentGuideNumberRequired, "The number of the guide that exists in Coordinadora is required.");
        }

        MarkCreated(guideNumber, null, now);
    }

    /// <summary>La persona miró en Coordinadora y no existe: <c>Failed</c>, el pedido queda libre.</summary>
    public void ResolveMissing(DateTimeOffset now)
    {
        EnsureUnknown();
        Status = CoordinadoraShipmentStatus.Failed;
        UpdatedAt = now;
    }

    /// <summary>Se anuló en Coordinadora: el pedido queda libre para otra guía. El rótulo guardado se descarta.</summary>
    public void MarkCancelled(Guid cancelledBy, DateTimeOffset now)
    {
        EnsureCreated("Only a guide created in Coordinadora can be cancelled.");
        Status = CoordinadoraShipmentStatus.Cancelled;
        LabelPdf = null;
        CancelledBy = cancelledBy;
        CancelledAt = now;
        UpdatedAt = now;
    }

    /// <summary>El último estado que dio <c>Guias_rastreoSimple</c>.</summary>
    public void RecordTracking(string? status, string? detail, DateTimeOffset now)
    {
        EnsureCreated("Only a guide created in Coordinadora can be tracked.");
        TrackingStatus = CoordinadoraText.Cap(status, TrackingStatusMaxLength);
        TrackingDetail = CoordinadoraText.Cap(detail, TrackingDetailMaxLength);
        TrackedAt = now;
        UpdatedAt = now;
    }

    public void EnsureCreated(string message)
    {
        if (!IsCreated)
        {
            throw new CoordinadoraDomainException(CoordinadoraErrorCodes.ShipmentStateInvalid, message);
        }
    }

    // La petición lleva nombre, dirección y teléfono del destinatario: el ToString no la escribe.
    public override string ToString() => $"CoordinadoraShipment {{ Id = {Id}, Status = {Status} }}";

    private void EnsureUnknown()
    {
        if (Status != CoordinadoraShipmentStatus.Unknown)
        {
            throw new CoordinadoraDomainException(
                CoordinadoraErrorCodes.ShipmentStateInvalid, "Only a guide in an unknown state can be resolved.");
        }
    }

    private void ClearError()
    {
        ErrorCode = null;
        ErrorDetail = null;
    }
}
