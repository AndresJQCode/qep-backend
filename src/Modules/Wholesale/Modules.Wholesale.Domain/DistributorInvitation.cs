namespace Modules.Wholesale.Domain;

/// <summary>
/// La invitación de un distribuidor, hija 1:1 de <see cref="Distributor"/> (D-1). En base sólo vive el SHA-256 del token:
/// el token plano viaja únicamente en el payload del outbox hacia el correo. Reenviar rota token y vigencia sobre la misma
/// fila, así que el link anterior deja de existir. <see cref="ReservedTenantId"/> es el id con que nace el tenant del
/// distribuidor si acepta creando uno (D-17): no cambia al reenviar.
/// </summary>
public sealed class DistributorInvitation
{
    // EF Core materializa por acá.
    private DistributorInvitation()
    {
        TokenHash = string.Empty;
    }

    private DistributorInvitation(string tokenHash, Guid reservedTenantId, DateTimeOffset sentAt, DateTimeOffset expiresAt)
    {
        TokenHash = tokenHash;
        ReservedTenantId = reservedTenantId;
        SentAt = sentAt;
        ExpiresAt = expiresAt;
    }

    public string TokenHash { get; private set; }

    public DateTimeOffset SentAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public Guid ReservedTenantId { get; private set; }

    public DateTimeOffset? AcceptedAt { get; private set; }

    public Guid? AcceptedByUserId { get; private set; }

    /// <summary>
    /// Spec 2026-11-18: arranca en 0 al emitir y sube con cada reenvío y con cada cambio de teléfono o conexión. El evento del
    /// aviso la lleva, y un resultado de otra generación no se registra (D-4).
    /// </summary>
    public int Generation { get; private set; }

    /// <summary>El aviso por WhatsApp de la generación vigente; <c>null</c> si no hay (sin teléfono o tras cambiarlo, P-3).</summary>
    public InvitationNoticeStatus? NoticeStatus { get; private set; }

    /// <summary>Sólo con <see cref="InvitationNoticeStatus.Failed"/>: <c>template_not_approved</c>,
    /// <c>connection_unavailable:&lt;motivo&gt;</c>, <c>send_failed:&lt;código&gt;</c> o <c>whatsapp_unavailable</c>.</summary>
    public string? NoticeFailureCode { get; private set; }

    public const int NoticeFailureCodeMaxLength = 128;

    /// <summary>El código de un reenvío cuya conexión guardada ya no está lista: el aviso falla sin encolarse.</summary>
    internal const string WhatsAppUnavailableCode = "whatsapp_unavailable";

    /// <summary>Vencida: <paramref name="now"/> pasó <see cref="ExpiresAt"/> (el instante exacto todavía vale, como en las
    /// membresías).</summary>
    public bool IsExpiredAt(DateTimeOffset now) => now > ExpiresAt;

    internal static DistributorInvitation Issue(string tokenHash, Guid reservedTenantId, bool withNotice, DateTimeOffset now) =>
        new(tokenHash, reservedTenantId, now, now + Distributor.InvitationTimeToLive)
        {
            NoticeStatus = withNotice ? InvitationNoticeStatus.Pending : null,
        };

    internal void Rotate(string tokenHash, DateTimeOffset now)
    {
        TokenHash = tokenHash;
        SentAt = now;
        ExpiresAt = now + Distributor.InvitationTimeToLive;
        Generation++;
    }

    /// <summary>Una generación nueva sin aviso: el que estaba en curso ya no es de este destino (P-3).</summary>
    internal void ForgetNotice()
    {
        Generation++;
        SetNotice(null, null);
    }

    internal void SetNotice(InvitationNoticeStatus? status, string? failureCode)
    {
        NoticeStatus = status;
        NoticeFailureCode = status == InvitationNoticeStatus.Failed ? Fit(failureCode) : null;
    }

    // El código lo arma quien entrega (prefijo + código de Mensajería); el tope de la columna no debe tumbar el registro.
    private static string? Fit(string? code) =>
        code is { Length: > NoticeFailureCodeMaxLength } ? code[..NoticeFailureCodeMaxLength] : code;

    internal void MarkAccepted(Guid userId, DateTimeOffset now)
    {
        AcceptedAt = now;
        AcceptedByUserId = userId;
    }
}
