using BuildingBlocks.Application;
using Modules.Buyers.Domain;

namespace Modules.Buyers.Application;

/// <summary>
/// Emite, exige y resuelve las sesiones de comprador por host. El token se muestra en claro una sola vez (al emitirlo) y sólo su hash
/// se guarda. Toda sesión que no sirve (ausente, desconocida, vencida por vida absoluta o por inactividad, revocada, de otro host o de
/// una cuenta borrada) es la misma <c>session_invalid</c>.
/// </summary>
public sealed class BuyerSessions(IBuyerSessionRepository sessions, IBuyersUnitOfWork unitOfWork, IClock clock)
{
    /// <summary>El token crudo mide 43 caracteres; más de esto no puede ser uno y no vale una consulta.</summary>
    public const int TokenMaxLength = 128;

    /// <summary>Crea la sesión y la guarda (junto con cualquier cambio pendiente de la unidad de trabajo).</summary>
    public async Task<(string Token, DateTimeOffset ExpiresAt)> IssueAsync(
        Guid accountId, string host, string? ipAddress, CancellationToken cancellationToken)
    {
        var token = BuyerTokenCodec.Generate();
        var session = BuyerSession.Issue(
            Guid.NewGuid(), accountId, host, BuyerTokenCodec.Hash(token), clock.UtcNow, ipAddress);
        sessions.Add(session);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return (token, session.ExpiresAt);
    }

    /// <summary>
    /// La sesión válida de ese host, o <c>null</c>. Un token nulo, vacío o demasiado largo no consulta nada. Avanza
    /// <c>LastSeenAt</c> (y guarda) sólo si pasó el umbral.
    /// </summary>
    public async Task<BuyerSession?> TryResolveAsync(string host, string? rawToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(rawToken) || rawToken.Length > TokenMaxLength)
        {
            return null;
        }

        var session = await sessions.FindByTokenHashAsync(BuyerTokenCodec.Hash(rawToken), cancellationToken);
        var now = clock.UtcNow;
        // Touch no mira la validez: se pregunta primero.
        if (session is null || !session.IsValidFor(host, now))
        {
            return null;
        }

        if (session.Touch(now))
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return session;
    }

    public async Task<BuyerSession> RequireAsync(string host, string? rawToken, CancellationToken cancellationToken) =>
        await TryResolveAsync(host, rawToken, cancellationToken)
        ?? throw new RequestUnauthorizedException(BuyerErrorCodes.SessionInvalid, "The buyer session is not valid.");

    public async Task RevokeOthersAsync(
        Guid accountId, Guid keepSessionId, string reason, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        foreach (var session in await sessions.ListActiveAsync(accountId, cancellationToken))
        {
            if (session.Id != keepSessionId)
            {
                session.Revoke(now, reason);
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task RevokeAllAsync(Guid accountId, string reason, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        foreach (var session in await sessions.ListActiveAsync(accountId, cancellationToken))
        {
            session.Revoke(now, reason);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
