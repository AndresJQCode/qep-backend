using BuildingBlocks.Domain;

namespace Modules.Support.Domain;

/// <summary>
/// Fase 4b (H15 a H21): la encuesta de satisfacción de un ciclo de resolución del ticket, una por <c>(ticket, número de resolución)</c>
/// (<c>UX_ticket_surveys_ticket_resolution</c>). La pide <see cref="SupportTicket.ChangeStatus"/> al pasar a Resuelto, por cualquier camino;
/// el worker decide el canal y la manda. Es una cola (la tabla tiene timbre) y no es prueba legal: se actualiza. Cambiarla no sube la
/// versión del ticket, para no darle un 412 al asesor que lo tiene abierto.
/// </summary>
public sealed class SupportTicketSurvey
{
    /// <summary>El comentario libre se corta en este largo.</summary>
    public const int CommentMaxLength = 1000;

    /// <summary>El largo de los códigos de motivo (los de <c>SupportChannelReasons</c>).</summary>
    public const int ReasonCodeMaxLength = 64;

    /// <summary>H14 y H21: el motivo de una encuesta que el tenant tenía apagada. Esa encuesta no se responde ni por la página de
    /// consulta. <c>SupportChannelReasons.SurveyDisabled</c> es este mismo valor.</summary>
    public const string DisabledReasonCode = "survey_disabled";

    /// <summary>H16: la espera antes de cada reintento de un envío fallido; después del último, la encuesta queda
    /// <see cref="SupportSurveyStatus.Failed"/>.</summary>
    public static IReadOnlyList<TimeSpan> RetryDelays { get; } =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    // EF Core materializa por acá; Request es el único punto de entrada de código.
    private SupportTicketSurvey()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public SupportTicketId TicketId { get; private set; }

    /// <summary>El <c>ResolutionCount</c> del ticket al resolver: qué ciclo juzga esta encuesta.</summary>
    public int ResolutionNumber { get; private set; }

    public SupportSurveyStatus Status { get; private set; }

    /// <summary>Por dónde salió (o, sin encuesta, por dónde habría salido).</summary>
    public SupportSurveyChannel? Channel { get; private set; }

    /// <summary>El motivo de <see cref="SupportSurveyStatus.Skipped"/> o del último fallo, uno de <c>SupportChannelReasons</c>.</summary>
    public string? ReasonCode { get; private set; }

    /// <summary>El mensaje de la plantilla por WhatsApp: el toque que lo cita es la respuesta (H18).</summary>
    public Guid? MessageId { get; private set; }

    /// <summary>H19: el SHA-256 del token del enlace de calificación; el token nunca se guarda.</summary>
    public byte[]? TokenHash { get; private set; }

    /// <summary>Los envíos que fallaron.</summary>
    public int Attempts { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    /// <summary>El reclamo del worker (<c>FOR UPDATE SKIP LOCKED</c>): mientras no venza, otro worker no la toma.</summary>
    public DateTimeOffset? LockedUntil { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    public SupportSurveyRating? Rating { get; private set; }

    public string? Comment { get; private set; }

    public DateTimeOffset? AnsweredAt { get; private set; }

    public SupportSurveyChannel? AnsweredVia { get; private set; }

    /// <summary>El toque de WhatsApp que calificó.</summary>
    public Guid? RatingMessageId { get; private set; }

    /// <summary>El mensaje de WhatsApp que se tomó como comentario.</summary>
    public Guid? CommentMessageId { get; private set; }

    /// <summary>H18: hasta cuándo el siguiente texto del cliente es el comentario (el toque más 24 horas).</summary>
    public DateTimeOffset? CommentUntil { get; private set; }

    /// <summary>H18: el mensaje que pidió el comentario después del toque, si salió.</summary>
    public Guid? PromptMessageId { get; private set; }

    public DateTimeOffset? PromptSentAt { get; private set; }

    /// <summary>H18: por qué no salió ese mensaje (un código de <c>SupportChannelReasons</c>); no se reintenta.</summary>
    public string? PromptFailureCode { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>La pide el ticket al resolver; lista para el worker desde ya.</summary>
    internal static SupportTicketSurvey Request(Guid tenantId, SupportTicketId ticketId, int resolutionNumber, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(resolutionNumber, 1);
        return new SupportTicketSurvey
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            TicketId = ticketId,
            ResolutionNumber = resolutionNumber,
            Status = SupportSurveyStatus.Pending,
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Salió por <paramref name="channel"/> (por la página de consulta, sin mensaje). <paramref name="reasonCode"/> explica un
    /// canal distinto del pedido, si lo hay.</summary>
    public void MarkSent(
        SupportSurveyChannel channel, Guid? messageId, byte[]? tokenHash, DateTimeOffset now, string? reasonCode = null)
    {
        EnsurePending();
        Status = SupportSurveyStatus.Sent;
        Channel = channel;
        MessageId = messageId;
        TokenHash = tokenHash is null ? null : [.. tokenHash];
        ReasonCode = reasonCode;
        SentAt = now;
        LockedUntil = null;
        UpdatedAt = now;
    }

    /// <summary>«Sin encuesta» por <paramref name="reasonCode"/> (no es una falla: no se reintenta).</summary>
    public void Skip(string reasonCode, SupportSurveyChannel? channel, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        EnsurePending();
        Status = SupportSurveyStatus.Skipped;
        Channel = channel;
        ReasonCode = reasonCode;
        LockedUntil = null;
        UpdatedAt = now;
    }

    /// <summary>H16: el envío falló. Vuelve a intentarse después de la espera de <see cref="RetryDelays"/> que le toca; pasado el
    /// último reintento queda <see cref="SupportSurveyStatus.Failed"/> con <paramref name="code"/>.</summary>
    public void Retry(string code, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        EnsurePending();
        Attempts++;
        ReasonCode = code;
        LockedUntil = null;
        UpdatedAt = now;
        if (Attempts > RetryDelays.Count)
        {
            Status = SupportSurveyStatus.Failed;
            return;
        }

        NextAttemptAt = now + RetryDelays[Attempts - 1];
    }

    /// <summary>El reclamo del worker: nadie más la toma hasta <paramref name="lockedUntil"/>.</summary>
    public void Claim(DateTimeOffset lockedUntil, DateTimeOffset now)
    {
        LockedUntil = lockedUntil;
        UpdatedAt = now;
    }

    /// <summary>
    /// H21 (owner 2026-11-16, Q-10): se responde una sola vez, cuando ya se procesó (no <see cref="SupportSurveyStatus.Pending"/>) y no
    /// es la de un tenant que la tenía apagada, el ticket está Resuelto o Cerrado y sigue en esta resolución: reabierto, esa resolución ya
    /// no se juzga.
    /// </summary>
    public bool IsAnswerable(SupportTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        return Rating is null
            && Status is not SupportSurveyStatus.Pending
            && !(Status is SupportSurveyStatus.Skipped && ReasonCode == DisabledReasonCode)
            && ticket.Id == TicketId
            && ticket.Status is SupportTicketStatus.Resolved or SupportTicketStatus.Closed
            && ticket.ResolutionCount == ResolutionNumber;
    }

    /// <summary>La calificación, con su comentario si vino. La primera gana: otra es <c>support.survey.already_answered</c>; una que ya
    /// no se responde es <c>support.survey.closed</c>. Por WhatsApp, <paramref name="commentUntil"/> abre la ventana del comentario
    /// (H18).</summary>
    public void Answer(
        SupportTicket ticket,
        SupportSurveyRating rating,
        string? comment,
        SupportSurveyChannel via,
        Guid? ratingMessageId,
        DateTimeOffset? commentUntil,
        DateTimeOffset now)
    {
        if (Rating is not null)
        {
            throw new SupportDomainException(SupportErrorCodes.SurveyAlreadyAnswered, "The survey was already answered.");
        }

        if (!IsAnswerable(ticket))
        {
            throw new SupportDomainException(SupportErrorCodes.SurveyClosed, "The survey can no longer be answered.");
        }

        Rating = rating;
        Comment = CommentOf(comment);
        AnsweredVia = via;
        AnsweredAt = now;
        RatingMessageId = ratingMessageId;
        CommentUntil = commentUntil;
        UpdatedAt = now;
    }

    /// <summary>H18: el comentario que llegó después del toque, una sola vez y antes de <see cref="CommentUntil"/>. Falso si no
    /// corresponde (sin calificar, ya con comentario o fuera de la ventana): el mensaje queda como un entrante cualquiera.</summary>
    public bool AddComment(string comment, Guid messageId, DateTimeOffset now)
    {
        if (Rating is null || Comment is not null || CommentUntil is not { } until || now >= until || CommentOf(comment) is not { } text)
        {
            return false;
        }

        Comment = text;
        CommentMessageId = messageId;
        UpdatedAt = now;
        return true;
    }

    /// <summary>H18: cómo terminó el mensaje que pide el comentario. Se anota una vez y no se reintenta.</summary>
    public void RecordPrompt(Guid? messageId, string? failureCode, DateTimeOffset now)
    {
        if (PromptSentAt is not null || PromptFailureCode is not null)
        {
            return;
        }

        if (messageId is { } sent)
        {
            PromptMessageId = sent;
            PromptSentAt = now;
        }
        else
        {
            PromptFailureCode = string.IsNullOrWhiteSpace(failureCode) ? "send_failed" : failureCode;
        }

        UpdatedAt = now;
    }

    // Recortado y cortado en 1000; vacío es null.
    private static string? CommentOf(string? comment)
    {
        var trimmed = comment?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : SnapshotText.Fit(trimmed, CommentMaxLength);
    }

    private void EnsurePending()
    {
        if (Status is not SupportSurveyStatus.Pending)
        {
            throw new InvalidOperationException($"The survey is already '{Status}'.");
        }
    }
}
