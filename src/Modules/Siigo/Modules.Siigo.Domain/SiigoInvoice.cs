namespace Modules.Siigo.Domain;

/// <summary>
/// Spec 2026-11-18 §5.6: una factura que QEP le pidió a Siigo, una fila por factura (<see cref="Kind"/>: la del pedido o, con
/// <c>freight_separate_invoice</c>, también la del flete). Guarda la petición entera (<see cref="RequestJson"/>) y su
/// <see cref="IdempotencyKey"/>: un reintento reenvía <b>los mismos bytes</b> con la misma clave, sin rearmar ni validar de nuevo, así
/// Siigo devuelve la que ya creó (la única excepción es <see cref="ResendAdjusted"/>, cuando Siigo la rechazó por el redondeo de los
/// pagos). <see cref="RequestJson"/> lleva datos del cliente: sólo lo leen los handlers del módulo, nunca sale en
/// un <c>GET</c> ni en un log, y el <c>ToString</c> no lo escribe. Una fila por pedido y tipo está «vigente» salvo que esté
/// <c>Rejected</c>, <c>Failed</c> o <c>Annulled</c> (índice único parcial <c>IX_invoices_tenant_order_kind</c>).
/// <para>«Por confirmar» (spec 2026-11-24 §3) no es un estado aparte: es una <c>Pending</c> con <see cref="UnansweredAt"/>, es decir,
/// con un envío del que no se sabe si Siigo creó la factura. Una así nunca pasa a <c>Rejected</c>: la reenvía una persona con la misma
/// clave o declara si existe en Siigo.</para>
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

    /// <summary>Cuánto dura el reclamo de un envío en curso. Cubre el peor caso de un pedido, con cada llamada hasta su plazo
    /// (autenticar, buscar y crear el cliente, autenticar, la factura y la del flete), porque nace antes de crear el cliente y también
    /// protege a la de flete, que sale después. Dentro de la ventana, otro «Generar factura» del mismo pedido no vuelve a mandar la
    /// petición que ya va en camino; si el proceso murió, pasada la ventana se retoma.</summary>
    public static readonly TimeSpan SendLease = TimeSpan.FromMinutes(8);

    /// <summary>Cuánto hay que esperar desde el último envío sin respuesta para declarar que la factura no existe: Siigo puede seguir
    /// procesándola después de que QEP deja de esperar, y «No existe» es la única acción que libera el pedido para facturar con otra
    /// clave.</summary>
    public static readonly TimeSpan MissingResolutionDelay = TimeSpan.FromMinutes(10);

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

    /// <summary>En un rechazo por pagos que no suman, el total que Siigo esperaba (se muestra junto al de QEP). Sólo ajusta los pagos si la
    /// diferencia es de redondeo (<see cref="ResendAdjusted"/>).</summary>
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

    /// <summary>Desde cuándo hay un envío del que no se sabe si Siigo creó la factura (el primero). Con valor, la fila ya no se puede
    /// rechazar.</summary>
    public DateTimeOffset? UnansweredAt { get; private set; }

    /// <summary>El último envío sin respuesta: desde ahí se cuenta <see cref="MissingResolutionDelay"/>.</summary>
    public DateTimeOffset? LastUnansweredAt { get; private set; }

    /// <summary>Id de membresía de quien la pidió, como <c>InvoicedBy</c> del pedido.</summary>
    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Vigente: cuenta para el pedido (todo menos <c>Rejected</c>, <c>Failed</c> y <c>Annulled</c>).</summary>
    public bool IsLive => Status is not (SiigoInvoiceStatus.Rejected or SiigoInvoiceStatus.Failed or SiigoInvoiceStatus.Annulled);

    /// <summary>Saliendo o sin resolver (<c>Pending</c> o <c>Unknown</c>): la marca manual del pedido espera.</summary>
    public bool IsOpen => Status is SiigoInvoiceStatus.Pending or SiigoInvoiceStatus.Unknown;

    /// <summary>Se puede mandar a la DIAN: existe en Siigo (con su id) y no está ya estampada.</summary>
    public bool CanStamp => Status is SiigoInvoiceStatus.Created or SiigoInvoiceStatus.StampRejected && SiigoInvoiceId is not null;

    /// <summary>Tiene PDF en Siigo: existe allá con su id (una <c>Created</c>, <c>Stamped</c> o <c>StampRejected</c>). Una resuelta a mano
    /// no tiene id, así que no se puede pedir.</summary>
    public bool HasPdf =>
        SiigoInvoiceId is not null && Status is (SiigoInvoiceStatus.Created or SiigoInvoiceStatus.Stamped or SiigoInvoiceStatus.StampRejected);

    /// <summary>Se puede preguntar a Siigo si está anulada (spec 2026-11-25 §1): existe allá con su id (una <c>Created</c>,
    /// <c>Stamped</c> o <c>StampRejected</c>). Una resuelta a mano no tiene id, así que no hay qué verificar.</summary>
    public bool IsAnnulmentVerifiable =>
        Status is (SiigoInvoiceStatus.Created or SiigoInvoiceStatus.Stamped or SiigoInvoiceStatus.StampRejected) && SiigoInvoiceId is not null;

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

    /// <summary>Se está por mandar la petición: cuenta el intento y reclama la fila durante <see cref="SendLease"/>. Antes anota el envío
    /// sin respuesta que la fila arrastre sin <see cref="UnansweredAt"/>, para que el resultado de éste no la rechace.</summary>
    public void BeginSend(DateTimeOffset now)
    {
        AdoptUnrecordedAttempt();
        Attempts++;
        SendingSince = now;
        UpdatedAt = now;
    }

    public bool IsBeingSent(DateTimeOffset now) => SendingSince is { } since && now - since < SendLease;

    /// <summary>La emisión se alargó (un reenvío por redondeo) mientras esta fila esperaba su turno: el reclamo cuenta desde ahora. No
    /// es un intento, y sin reclamo no hace nada.</summary>
    public void RenewClaim(DateTimeOffset now)
    {
        if (SendingSince is not null)
        {
            SendingSince = now;
            UpdatedAt = now;
        }
    }

    /// <summary>Siigo rechazó los pagos por una diferencia de redondeo: se manda otra petición, así que lleva otra clave y cuenta como
    /// un intento nuevo. Sólo sin envíos sin respuesta: con uno, Siigo pudo haber creado la primera y otra clave la duplicaría.</summary>
    public void ResendAdjusted(string requestJson, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestJson);
        if (Status != SiigoInvoiceStatus.Pending || UnansweredAt is not null)
        {
            throw new SiigoDomainException(
                SiigoErrorCodes.InvoiceStateInvalid, "Only a pending invoice that Siigo has answered every time can be sent with other payments.");
        }

        RequestJson = requestJson;
        IdempotencyKey = SiigoIdempotencyKey.For(Guid.CreateVersion7());
        ClearError();
        BeginSend(now);
    }

    /// <summary>La petición no llegó a salir (la de flete que espera a la del pedido, o una falla de configuración antes de enviar):
    /// sigue <c>Pending</c> sin quedar por confirmar y se suelta el reclamo. Un envío que sí salió y no tuvo respuesta va por
    /// <see cref="MarkUnanswered"/>.</summary>
    /// <param name="errorCode">Por qué falló este intento (<c>siigo.invoice.*</c>), para que el pedido se lo diga a quien factura. Nulo
    /// conserva el que ya había: es la factura de flete que espera a la del pedido, que no falló por sí misma.</param>
    public void MarkTransient(string? errorCode, DateTimeOffset now)
    {
        if (errorCode is not null)
        {
            ErrorCode = Cap(errorCode, ErrorCodeMaxLength);
            ErrorDetail = null;
            ExpectedPaymentsTotal = null;
        }

        SendingSince = null;
        UpdatedAt = now;
    }

    /// <summary>El envío salió y no se sabe si Siigo creó la factura (timeout, red, 5xx, 429 o un servicio suyo caído): sigue
    /// <c>Pending</c> por confirmar, con el motivo, y se suelta el reclamo para que el reenvío mande los mismos bytes con la misma
    /// clave.</summary>
    /// <returns>Si es el primer envío sin respuesta de la fila (la que pasa a estar por confirmar).</returns>
    public bool MarkUnanswered(string errorCode, string? errorDetail, DateTimeOffset now)
    {
        var first = UnansweredAt is null;
        UnansweredAt ??= now;
        LastUnansweredAt = now;
        KeepPending(errorCode, errorDetail, now);
        return first;
    }

    /// <summary>
    /// Siigo contestó un error. Sólo queda <c>Rejected</c> (y libera el pedido) si es un dato rechazado y no hay ningún envío anterior
    /// sin respuesta: una caída de servicio no dice si el documento quedó creado, y después de un envío sin respuesta Siigo pudo haber
    /// creado la primera y contestar otra cosa al reenvío. En esos dos casos sigue <c>Pending</c> por confirmar.
    /// </summary>
    /// <param name="serviceOutage">Todos los errores son de servicio de Siigo.</param>
    /// <returns>Si quedó <c>Rejected</c>.</returns>
    public bool RecordRejection(
        string errorCode, string errorDetail, decimal? expectedPaymentsTotal, bool serviceOutage, DateTimeOffset now)
    {
        if (serviceOutage)
        {
            MarkUnanswered(errorCode, errorDetail, now);
            return false;
        }

        if (UnansweredAt is not null)
        {
            // Siigo sí contestó este reenvío: LastUnansweredAt no se mueve.
            KeepPending(errorCode, errorDetail, now);
            ExpectedPaymentsTotal = expectedPaymentsTotal;
            return false;
        }

        MarkRejected(errorCode, errorDetail, expectedPaymentsTotal, now);
        return true;
    }

    /// <summary>Antes de reenviar, anota el envío anterior que quedó sin respuesta y sin registrar: uno cuyo reclamo venció sin resultado
    /// (el proceso murió o se cortó por un redespliegue) o el que la fila arrastra sin <see cref="UnansweredAt"/>.</summary>
    /// <returns>Si con esto la fila pasa a estar por confirmar por primera vez.</returns>
    public bool RecoverInterruptedSend(DateTimeOffset now)
    {
        if (Status != SiigoInvoiceStatus.Pending || IsBeingSent(now))
        {
            return false;
        }

        var adopted = AdoptUnrecordedAttempt();
        if (SendingSince is null)
        {
            return adopted;
        }

        var first = MarkUnanswered(SiigoErrorCodes.InvoiceInterrupted, null, now);
        return adopted || first;
    }

    /// <summary>Por confirmar: <c>Pending</c>, nadie la está mandando y hay un envío sin respuesta, registrado
    /// (<see cref="UnansweredAt"/>), con el reclamo vencido sin resultado (<see cref="SendingSince"/>) o que sólo se ve por el motivo de
    /// la fila.</summary>
    public bool IsAwaitingConfirmation(DateTimeOffset now) =>
        Status == SiigoInvoiceStatus.Pending
        && (UnansweredAt is not null || SendingSince is not null || UnrecordedAttemptAt is not null)
        && !IsBeingSent(now);

    /// <summary>La puede resolver una persona: <c>Unknown</c>, o <c>Pending</c> que nadie está mandando.</summary>
    public bool IsResolvable(DateTimeOffset now) =>
        Status == SiigoInvoiceStatus.Unknown || (Status == SiigoInvoiceStatus.Pending && !IsBeingSent(now));

    /// <summary>Si la pantalla ofrece «Existe en Siigo» / «No existe» (spec 2026-11-24 §5): una <c>Unknown</c> (también la <c>Pending</c>
    /// que ya pasó <see cref="StaleAfter"/> y que la próxima lectura del pedido expira) o una por confirmar. Es más estrecho que
    /// <see cref="IsResolvable"/>: una que nunca salió se puede resolver, pero ofrecerlo confunde.</summary>
    public bool OffersResolution(DateTimeOffset now) =>
        Status == SiigoInvoiceStatus.Unknown || IsStale(now) || IsAwaitingConfirmation(now);

    /// <summary>Se puede reenviar con la misma clave: <c>Pending</c>, nadie la está mandando y no ha pasado
    /// <see cref="StaleAfter"/>.</summary>
    public bool CanResend(DateTimeOffset now) =>
        Status == SiigoInvoiceStatus.Pending && !IsBeingSent(now) && now - CreatedAt <= StaleAfter;

    /// <summary>Desde cuándo se puede declarar que no existe, si todavía hay que esperar. <c>null</c> si no es resoluble o si ya se
    /// puede. Vale también para una <c>Unknown</c>: se reenvió poco antes de las 24 h, Siigo puede seguir procesándola. Una sin ningún
    /// envío sin respuesta no tiene nada que esperar.</summary>
    public DateTimeOffset? MissingAvailableAt(DateTimeOffset now)
    {
        if (!IsResolvable(now))
        {
            return null;
        }

        // El reclamo vencido sin resultado es más reciente que el último envío sin respuesta registrado.
        var availableAt = (SendingSince ?? LastUnansweredAt ?? UnrecordedAttemptAt) + MissingResolutionDelay;
        return availableAt > now ? availableAt : null;
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
    /// al leerla o reintentarla. Una que se está mandando no expira (se resolvería con la petición en camino). El envío sin respuesta que
    /// tuviera se conserva: de él depende <see cref="MissingAvailableAt"/>. Devuelve si cambió.</summary>
    public bool ExpireIfStale(DateTimeOffset now)
    {
        if (!IsStale(now))
        {
            return false;
        }

        AdoptUnrecordedAttempt();
        if (SendingSince is { } deadSend)
        {
            UnansweredAt ??= deadSend;
            LastUnansweredAt = deadSend;
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
        EnsureResolvable(now);
        if (string.IsNullOrWhiteSpace(number))
        {
            throw new SiigoDomainException(SiigoErrorCodes.InvoiceStateInvalid, "The number of the invoice that exists in Siigo is required.");
        }

        Number = Cap(number.Trim(), NumberMaxLength);
        Status = SiigoInvoiceStatus.Created;
        ClearError();
        SendingSince = null;
        UpdatedAt = now;
    }

    /// <summary>La persona miró en Siigo y no existe: <c>Failed</c>, el pedido queda libre para facturar de nuevo. Antes de
    /// <see cref="MissingResolutionDelay"/> desde el último envío sin respuesta es 422 <c>siigo.invoice.resolve_too_early</c>.</summary>
    public void ResolveMissing(DateTimeOffset now)
    {
        EnsureResolvable(now);
        if (MissingAvailableAt(now) is { } availableAt)
        {
            throw new SiigoResolveTooEarlyException(availableAt);
        }

        Status = SiigoInvoiceStatus.Failed;
        SendingSince = null;
        UpdatedAt = now;
    }

    /// <summary>QEP verificó en Siigo que una persona la anuló o la borró allá (spec 2026-11-25 §1): <c>Annulled</c>, deja de ser
    /// vigente y el motivo viaja en <see cref="ErrorCode"/> (pisa el rechazo de la DIAN de una <c>StampRejected</c>). Conserva el id de
    /// Siigo, el número, el CUFE y el total: es el registro de un documento que existió.</summary>
    /// <param name="deleted">Siigo contestó que ya no existe; con <c>false</c>, que está anulada.</param>
    public void MarkAnnulled(bool deleted, DateTimeOffset now)
    {
        if (!IsAnnulmentVerifiable)
        {
            throw new SiigoDomainException(
                SiigoErrorCodes.InvoiceStateInvalid, "Only an invoice that exists in Siigo with its id can be marked as annulled.");
        }

        Status = SiigoInvoiceStatus.Annulled;
        ErrorCode = deleted ? SiigoErrorCodes.InvoiceDeletedInSiigo : SiigoErrorCodes.InvoiceAnnulledInSiigo;
        ErrorDetail = null;
        ExpectedPaymentsTotal = null;
        SendingSince = null;
        UpdatedAt = now;
    }

    public void MarkOrderChanged() => OrderChanged = true;

    // Un record imprimiría la petición (con el documento y la dirección del cliente) en cualquier log; esta clase no es un record, pero
    // el ToString por defecto sólo escribe el tipo: se deja explícito para que nadie lo «mejore».
    public override string ToString() => $"SiigoInvoice {{ Id = {Id}, Kind = {Kind}, Status = {Status} }}";

    // Durante el despliegue la versión anterior sigue escribiendo, después del relleno de la migración, filas Pending con el motivo y sin
    // unanswered_at. Estos motivos sólo los deja un envío que salió; `unavailable` no entra porque también lo deja uno que nunca salió
    // (D-2), y esas se cubren corriendo otra vez el relleno. UpdatedAt es el momento de ese envío mientras nadie la vuelva a tocar.
    private DateTimeOffset? UnrecordedAttemptAt =>
        Status == SiigoInvoiceStatus.Pending
        && UnansweredAt is null
        && ErrorCode is SiigoErrorCodes.InvoiceTimeout or SiigoErrorCodes.InvoiceNetwork
            or SiigoErrorCodes.InvoiceRateLimited or SiigoErrorCodes.InvoiceSiigoService
            ? UpdatedAt
            : null;

    // Lo que ya no se reenvía solo: una Pending de más de StaleAfter que nadie está mandando.
    private bool IsStale(DateTimeOffset now) =>
        Status == SiigoInvoiceStatus.Pending && now - CreatedAt > StaleAfter && !IsBeingSent(now);

    private bool AdoptUnrecordedAttempt()
    {
        if (UnrecordedAttemptAt is not { } attemptedAt)
        {
            return false;
        }

        UnansweredAt = attemptedAt;
        LastUnansweredAt = attemptedAt;
        return true;
    }

    private void KeepPending(string errorCode, string? errorDetail, DateTimeOffset now)
    {
        ErrorCode = Cap(errorCode, ErrorCodeMaxLength);
        ErrorDetail = errorDetail is null ? null : Cap(errorDetail, ErrorDetailMaxLength);
        ExpectedPaymentsTotal = null;
        SendingSince = null;
        UpdatedAt = now;
    }

    private void EnsureResolvable(DateTimeOffset now)
    {
        if (!IsResolvable(now))
        {
            throw new SiigoDomainException(
                SiigoErrorCodes.InvoiceStateInvalid, "Only an unknown invoice, or a pending one that is not being sent, can be resolved.");
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
