using System.Globalization;
using System.Security.Cryptography;
using BuildingBlocks.Application;
using Modules.Identity.Domain;

namespace Modules.Identity.Application;

/// <summary>
/// Spec 2026-11-19 (onboarding por link) §4: confirma un correo con un código de un solo uso antes de que exista el usuario.
/// El código se consume en la misma llamada que crea el usuario, así que no hay prueba que guardar entre pasos.
/// </summary>
public interface IEmailChallenges
{
    /// <summary>
    /// Genera un código de <see cref="EmailChallengeService.CodeLength"/> dígitos, guarda sólo su SHA-256 junto al hash del
    /// correo normalizado, invalida el desafío anterior de ese correo y publica
    /// <see cref="IdentityIntegrationEvents.EmailChallengeRequested"/> con el código crudo por outbox. Si ya se emitió uno hace
    /// menos de <see cref="EmailChallengeService.ReissueInterval"/>, no emite otro y no falla.
    /// </summary>
    /// <exception cref="IdentityDomainException">El correo no es una dirección válida (<c>User.NormalizeEmail</c>).</exception>
    Task IssueAsync(string email, CancellationToken cancellationToken);

    /// <summary>
    /// Atómico y fuera de la unidad de trabajo, como <see cref="IEmailTokenRepository.TryConsumeAsync"/>. Un código errado suma
    /// un intento; al intento <see cref="EmailChallengeService.MaxAttempts"/> el desafío queda inválido aunque el siguiente
    /// código sea el correcto. Un correo mal formado, vencido, ya usado o sin desafío es <c>false</c>, sin distinguirlos.
    /// </summary>
    Task<bool> TryConsumeAsync(string email, string code, CancellationToken cancellationToken);
}

/// <summary>Lo que se guarda y lo que se consulta del desafío, todo por hash.</summary>
public interface IEmailChallengeRepository
{
    void Add(EmailChallenge challenge);

    /// <summary>Cuándo se emitió el último desafío de ese correo, en cualquier estado; <c>null</c> si nunca.</summary>
    Task<DateTimeOffset?> FindLatestIssuedAtAsync(string emailHash, CancellationToken cancellationToken);

    /// <summary>Marca como usados los desafíos vigentes del correo. Carga y marca: entra en la unidad de trabajo del
    /// llamador.</summary>
    Task InvalidateUnusedAsync(string emailHash, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Una sola sentencia que se confirma sola: si el código coincide, marca el desafío como usado y devuelve <c>true</c>; si
    /// no, suma un intento y, al llegar a <paramref name="maxAttempts"/>, lo marca como usado.
    /// </summary>
    Task<bool> TryConsumeAsync(
        string emailHash, string codeHash, int maxAttempts, DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed class EmailChallengeService(
    IEmailChallengeRepository challenges,
    IIdentityOutbox outbox,
    IIdentityUnitOfWork unitOfWork,
    IClock clock)
    : IEmailChallenges
{
    public const int CodeLength = 6;

    public const int MaxAttempts = 5;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan ReissueInterval = TimeSpan.FromSeconds(60);

    private const int CodeSpace = 1_000_000;

    public async Task IssueAsync(string email, CancellationToken cancellationToken)
    {
        var normalizedEmail = User.NormalizeEmail(email);
        var emailHash = EmailTokenCodec.Hash(normalizedEmail);
        var now = clock.UtcNow;

        // Un reenvío dentro del intervalo no es un error: la pantalla puede repetir el pedido y el código anterior sigue vivo.
        var latest = await challenges.FindLatestIssuedAtAsync(emailHash, cancellationToken);
        if (latest is { } issuedAt && now - issuedAt < ReissueInterval)
        {
            return;
        }

        await challenges.InvalidateUnusedAsync(emailHash, now, cancellationToken);

        var code = RandomNumberGenerator.GetInt32(CodeSpace).ToString("D6", CultureInfo.InvariantCulture);
        var challenge = EmailChallenge.Issue(
            EmailChallengeId.New(), emailHash, EmailTokenCodec.Hash(code), now, now + Lifetime);
        challenges.Add(challenge);

        // El código crudo viaja sólo en el payload del outbox, que lo entrega por correo. Sin request que lo origine, la
        // correlación es el propio desafío.
        outbox.Add(
            IdentityIntegrationEvents.EmailChallengeRequested,
            new EmailChallengeRequestedPayload(challenge.Id.Value, normalizedEmail, code, challenge.ExpiresAt),
            challenge.Id.ToString(),
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> TryConsumeAsync(string email, string code, CancellationToken cancellationToken)
    {
        string normalizedEmail;
        try
        {
            normalizedEmail = User.NormalizeEmail(email ?? string.Empty);
        }
        catch (IdentityDomainException)
        {
            // Un correo mal formado no puede tener desafío: mismo resultado que un código errado.
            return false;
        }

        return await challenges.TryConsumeAsync(
            EmailTokenCodec.Hash(normalizedEmail),
            EmailTokenCodec.Hash((code ?? string.Empty).Trim()),
            MaxAttempts,
            clock.UtcNow,
            cancellationToken);
    }
}
