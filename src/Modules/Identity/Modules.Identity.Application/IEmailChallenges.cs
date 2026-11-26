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
    /// menos de <see cref="EmailChallengeService.ReissueInterval"/>, o ya van <see cref="EmailChallengeService.MaxIssuesPerDay"/>
    /// en las últimas <see cref="EmailChallengeService.IssueWindow"/>, no emite otro y no falla: por fuera se ve igual, así que
    /// no revela si el correo tiene cuenta. Todo corre bajo un candado por correo, así que queda a lo sumo un desafío vivo.
    /// </summary>
    /// <exception cref="IdentityDomainException">El correo no es una dirección válida (<c>User.NormalizeEmail</c>).</exception>
    Task IssueAsync(string email, CancellationToken cancellationToken);

    /// <summary>
    /// Atómico y fuera de la unidad de trabajo, como <see cref="IEmailTokenRepository.TryConsumeAsync"/>. Un código errado suma
    /// un intento; al intento <see cref="EmailChallengeService.MaxAttempts"/> el desafío queda inválido aunque el siguiente
    /// código sea el correcto. Sólo cuenta el desafío más reciente del correo: el código de uno anterior es un código errado.
    /// Un correo mal formado, vencido, ya usado o sin desafío es <c>false</c>, sin distinguirlos.
    /// </summary>
    Task<bool> TryConsumeAsync(string email, string code, CancellationToken cancellationToken);
}

/// <summary>
/// El candado de emisión de un correo: abre la transacción y la sostiene hasta <see cref="CommitAsync"/>. Disponerlo sin
/// confirmar deshace lo escrito y suelta el candado.
/// </summary>
public interface IEmailChallengeIssuanceLock : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>Lo que se guarda y lo que se consulta del desafío, todo por hash.</summary>
public interface IEmailChallengeRepository
{
    void Add(EmailChallenge challenge);

    /// <summary>
    /// Abre una transacción y toma el candado de emisión de ese correo. Dos emisiones del mismo correo van en fila: la segunda
    /// lee lo que la primera confirmó. Lo que se guarde con la unidad de trabajo mientras se sostiene entra en esa transacción.
    /// </summary>
    Task<IEmailChallengeIssuanceLock> LockIssuanceAsync(string emailHash, CancellationToken cancellationToken);

    /// <summary>Cuándo se emitieron los desafíos de ese correo después de <paramref name="since"/>, en cualquier estado.</summary>
    Task<IReadOnlyList<DateTimeOffset>> ListIssuedSinceAsync(
        string emailHash, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Marca como usados los desafíos vigentes del correo. Carga y marca: entra en la unidad de trabajo del
    /// llamador.</summary>
    Task InvalidateUnusedAsync(string emailHash, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Una sola sentencia que se confirma sola, sobre el desafío más reciente del correo y ningún otro: si el código coincide,
    /// lo marca como usado y devuelve <c>true</c>; si no, suma un intento y, al llegar a <paramref name="maxAttempts"/>, lo
    /// marca como usado.
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

    /// <summary>Tope de desafíos por correo en <see cref="IssueWindow"/> (ventana móvil). Con <see cref="MaxAttempts"/> acota los
    /// códigos que se pueden probar contra un correo en un día.</summary>
    public const int MaxIssuesPerDay = 5;

    public static readonly TimeSpan IssueWindow = TimeSpan.FromHours(24);

    private const int CodeSpace = 1_000_000;

    public async Task IssueAsync(string email, CancellationToken cancellationToken)
    {
        var normalizedEmail = User.NormalizeEmail(email);
        var emailHash = EmailTokenCodec.Hash(normalizedEmail);

        // Revisión final de la rama, hallazgo 1: leer y luego insertar dejaba varios desafíos vivos con pedidos simultáneos, y
        // cada intento probaba todos sus códigos. Bajo el candado, el segundo pedido ve el desafío del primero.
        await using var issuance = await challenges.LockIssuanceAsync(emailHash, cancellationToken);
        var now = clock.UtcNow;

        // Un reenvío dentro del intervalo o por encima del tope no es un error: la pantalla puede repetir el pedido, y responder
        // distinto revelaría algo del correo.
        var issued = await challenges.ListIssuedSinceAsync(emailHash, now - IssueWindow, cancellationToken);
        if (issued.Count >= MaxIssuesPerDay || issued.Any(issuedAt => now - issuedAt < ReissueInterval))
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
        await issuance.CommitAsync(cancellationToken);
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
