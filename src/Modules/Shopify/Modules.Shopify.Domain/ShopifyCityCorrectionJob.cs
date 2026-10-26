namespace Modules.Shopify.Domain;

/// <summary>Qué hace el trabajo (spec 2026-10-26 D-C6): aplicar la ciudad de un sinónimo recién confirmado a lo que esperaba por
/// él, o llevar a la ciudad nueva lo que un sinónimo corregido ya había escrito (§4.6).</summary>
public enum ShopifyCityCorrectionJobKind
{
    Resolve,
    Recorrect,
}

/// <summary><see cref="Failed"/> lo pone la cola al agotar los intentos; un trabajo que no tiene nada que hacer termina
/// <see cref="Skipped"/> con su motivo.</summary>
public enum ShopifyCityCorrectionJobStatus
{
    Pending,
    Applied,
    Skipped,
    Failed,
}

/// <summary>
/// Spec 2026-10-26 D-C6 (<c>shopify.city_correction_jobs</c>): una fila por pedido o cliente que un sinónimo puede arreglar (desvío
/// S5), porque pedido y cliente viven en módulos y unidades de trabajo distintos y un fallo en uno no debe frenar los demás. La
/// siembra el consumidor de los eventos de Geography (<see cref="SourceMessageId"/> = el mensaje del outbox) o el importador, si el
/// sinónimo se confirmó en medio de una importación (sin mensaje). El reclamo (<see cref="Attempts"/>, <see cref="LeaseUntil"/>) y
/// el <see cref="ShopifyCityCorrectionJobStatus.Failed"/> los escribe la cola por SQL, fuera del agregado.
/// </summary>
public sealed class ShopifyCityCorrectionJob
{
    public const int LastErrorMaxLength = 512;

    /// <summary>Holgado para el motivo más largo (<c>customer_not_completable</c>, 24).</summary>
    public const int SkipReasonMaxLength = 32;

    private ShopifyCityCorrectionJob()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>El tenant del pedido o del cliente, no el del operador que confirmó: la corrección y su auditoría son de ese tenant.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>Referencia blanda a <c>geography.city_synonyms</c>: el trabajo lee la ciudad <b>actual</b> del sinónimo al correr.</summary>
    public Guid SynonymId { get; private set; }

    public ShopifyCityCorrectionJobKind Kind { get; private set; }

    public ShopifyCityCorrectionTarget Target { get; private set; }

    /// <summary>El pedido de Quotations o el cliente de Customers, según <see cref="Target"/>.</summary>
    public Guid TargetId { get; private set; }

    /// <summary>La fila de <c>shopify.orders</c> de la que se leen los datos del pedido; un <c>Recorrect</c> de un cliente puede no
    /// tenerla.</summary>
    public Guid? MirrorOrderId { get; private set; }

    /// <summary>La huella que recorrige un <see cref="ShopifyCityCorrectionJobKind.Recorrect"/>; <c>null</c> en un <c>Resolve</c>.</summary>
    public Guid? CorrectionId { get; private set; }

    /// <summary>Quién confirmó o corrigió el sinónimo, para la auditoría; <see cref="Guid.Empty"/> si lo sembró el importador.</summary>
    public Guid OperatorSubjectId { get; private set; }

    /// <summary>El mensaje del outbox que lo sembró; con él, el índice único (mensaje, destino, id) impide sembrarlo dos veces.</summary>
    public Guid? SourceMessageId { get; private set; }

    public ShopifyCityCorrectionJobStatus Status { get; private set; }

    public string? SkipReason { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset? LeaseUntil { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public static ShopifyCityCorrectionJob Resolve(
        Guid tenantId,
        Guid synonymId,
        ShopifyCityCorrectionTarget target,
        Guid targetId,
        Guid mirrorOrderId,
        Guid operatorSubjectId,
        Guid? sourceMessageId,
        DateTimeOffset now)
    {
        // El Resolve lee del espejo la dirección o los datos del comprador: sin fila no tiene de dónde.
        ArgumentOutOfRangeException.ThrowIfEqual(mirrorOrderId, Guid.Empty);
        return New(
            tenantId, synonymId, ShopifyCityCorrectionJobKind.Resolve, target, targetId, mirrorOrderId, null, operatorSubjectId,
            sourceMessageId, now);
    }

    public static ShopifyCityCorrectionJob Recorrect(
        ShopifyCityCorrection correction, Guid operatorSubjectId, Guid? sourceMessageId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(correction);
        return New(
            correction.TenantId, correction.SynonymId, ShopifyCityCorrectionJobKind.Recorrect, correction.Target, correction.TargetId,
            correction.MirrorOrderId, correction.Id, operatorSubjectId, sourceMessageId, now);
    }

    public void MarkApplied(DateTimeOffset now)
    {
        EnsurePending();
        Status = ShopifyCityCorrectionJobStatus.Applied;
        ProcessedAt = now;
    }

    /// <summary>El trabajo no tenía nada que hacer: el pedido ya no está pendiente, alguien editó la parte, el sinónimo se borró…
    /// Termina sin escribir nada; <paramref name="reason"/> es uno de <c>ShopifyCityCorrectionSkipReasons</c>.</summary>
    public void MarkSkipped(string reason, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(reason.Length, SkipReasonMaxLength, nameof(reason));
        EnsurePending();
        Status = ShopifyCityCorrectionJobStatus.Skipped;
        SkipReason = reason;
        ProcessedAt = now;
    }

    private static ShopifyCityCorrectionJob New(
        Guid tenantId,
        Guid synonymId,
        ShopifyCityCorrectionJobKind kind,
        ShopifyCityCorrectionTarget target,
        Guid targetId,
        Guid? mirrorOrderId,
        Guid? correctionId,
        Guid operatorSubjectId,
        Guid? sourceMessageId,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(tenantId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(synonymId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(targetId, Guid.Empty);
        if (!Enum.IsDefined(target))
        {
            throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown correction target.");
        }

        return new ShopifyCityCorrectionJob
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SynonymId = synonymId,
            Kind = kind,
            Target = target,
            TargetId = targetId,
            MirrorOrderId = mirrorOrderId,
            CorrectionId = correctionId,
            OperatorSubjectId = operatorSubjectId,
            SourceMessageId = sourceMessageId,
            Status = ShopifyCityCorrectionJobStatus.Pending,
            CreatedAt = now,
        };
    }

    private void EnsurePending()
    {
        if (Status != ShopifyCityCorrectionJobStatus.Pending)
        {
            throw new InvalidOperationException($"The city correction job is {Status}, not Pending.");
        }
    }
}
