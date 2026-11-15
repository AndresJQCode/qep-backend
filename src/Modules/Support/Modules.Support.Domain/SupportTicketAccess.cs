namespace Modules.Support.Domain;

/// <summary>
/// La clave de consulta de un ticket y su bloqueo (spec §9.4, plan F7). Vive aparte de <see cref="SupportTicket"/> a propósito: si
/// cada intento fallido tocara el ticket, le subiría la versión y le daría un 412 al asesor que lo tiene abierto.
/// <see cref="MaxFailures"/> fallos seguidos bloquean la consulta de ese radicado durante <see cref="LockFor"/>, también para el PIN
/// correcto; un acierto pone el contador en cero, y un reenvío del PIN no desbloquea.
/// </summary>
public sealed class SupportTicketAccess
{
    public const int MaxFailures = 5;

    public const int PinHashMaxLength = 256;

    /// <summary>El canal del último envío de la información con el PIN: <c>Email</c>, <c>Thread</c> o <c>None</c>.</summary>
    public const int ChannelMaxLength = 16;

    public const int FailureCodeMaxLength = 64;

    public static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);

    /// <summary>El enfriamiento de «Reenviar PIN» (plan F10).</summary>
    public static readonly TimeSpan ResendCooldown = TimeSpan.FromMinutes(10);

    // EF Core materializa por acá; Issue es el único punto de entrada de código.
    private SupportTicketAccess()
    {
        PinHash = string.Empty;
    }

    public SupportTicketId TicketId { get; private set; }

    public Guid TenantId { get; private set; }

    public string PinHash { get; private set; }

    public int FailedAttempts { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public DateTimeOffset PinIssuedAt { get; private set; }

    /// <summary>Cuándo se intentó el último envío, haya salido o no: el enfriamiento cuenta desde el intento.</summary>
    public DateTimeOffset? LastSentAt { get; private set; }

    public string? LastSentChannel { get; private set; }

    /// <summary>Por qué no salió el último envío (<c>send_failed</c>, <c>window_closed</c>, <c>no_author</c>…); null si salió.</summary>
    public string? LastFailureCode { get; private set; }

    public static SupportTicketAccess Issue(SupportTicketId ticketId, Guid tenantId, string pinHash, DateTimeOffset now) =>
        new()
        {
            TicketId = ticketId,
            TenantId = tenantId,
            PinHash = ValidHash(pinHash),
            PinIssuedAt = now,
        };

    public bool IsLocked(DateTimeOffset now) => LockedUntil is { } until && now < until;

    /// <summary>El fallo número <see cref="MaxFailures"/> bloquea hasta <c>now + LockFor</c> y deja el contador en cero: al vencer el
    /// bloqueo hay otros <see cref="MaxFailures"/> intentos.</summary>
    public void RecordFailure(DateTimeOffset now)
    {
        FailedAttempts++;
        if (FailedAttempts >= MaxFailures)
        {
            LockedUntil = now + LockFor;
            FailedAttempts = 0;
        }
    }

    public void RecordSuccess() => FailedAttempts = 0;

    /// <summary>Un PIN nuevo («Reenviar PIN»). No toca el bloqueo ni el contador: reenviar no es una vía para desbloquear.</summary>
    public void Reissue(string pinHash, DateTimeOffset now)
    {
        PinHash = ValidHash(pinHash);
        PinIssuedAt = now;
    }

    public bool CanResend(DateTimeOffset now) => LastSentAt is not { } last || now - last >= ResendCooldown;

    public void RecordDelivery(string channel, string? failureCode, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(channel) || channel.Trim().Length > ChannelMaxLength)
        {
            throw new ArgumentException($"The channel must have between 1 and {ChannelMaxLength} characters.", nameof(channel));
        }

        if (failureCode is not null && (string.IsNullOrWhiteSpace(failureCode) || failureCode.Length > FailureCodeMaxLength))
        {
            throw new ArgumentException(
                $"The failure code must have between 1 and {FailureCodeMaxLength} characters.", nameof(failureCode));
        }

        LastSentChannel = channel.Trim();
        LastFailureCode = failureCode;
        LastSentAt = now;
    }

    private static string ValidHash(string pinHash) =>
        string.IsNullOrWhiteSpace(pinHash) || pinHash.Length > PinHashMaxLength
            ? throw new ArgumentException($"The pin hash must have between 1 and {PinHashMaxLength} characters.", nameof(pinHash))
            : pinHash;
}
