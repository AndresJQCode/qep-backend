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

    /// <summary>Vencida: <paramref name="now"/> pasó <see cref="ExpiresAt"/> (el instante exacto todavía vale, como en las
    /// membresías).</summary>
    public bool IsExpiredAt(DateTimeOffset now) => now > ExpiresAt;

    internal static DistributorInvitation Issue(string tokenHash, Guid reservedTenantId, DateTimeOffset now) =>
        new(tokenHash, reservedTenantId, now, now + Distributor.InvitationTimeToLive);

    internal void Rotate(string tokenHash, DateTimeOffset now)
    {
        TokenHash = tokenHash;
        SentAt = now;
        ExpiresAt = now + Distributor.InvitationTimeToLive;
    }

    internal void MarkAccepted(Guid userId, DateTimeOffset now)
    {
        AcceptedAt = now;
        AcceptedByUserId = userId;
    }
}
