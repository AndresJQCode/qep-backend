using BuildingBlocks.Application;
using Modules.Buyers.Domain;

namespace Modules.Buyers.Application;

/// <summary>
/// Emite, exige y resuelve las sesiones de comprador por host. El token se muestra en claro una sola vez (al emitirlo) y sólo su hash
/// se guarda. Toda sesión que no sirve (ausente, desconocida, vencida por vida absoluta o por inactividad, revocada, de otro host o de
/// una cuenta borrada) es la misma <c>session_invalid</c>.
/// </summary>
/// <remarks>
/// La resolución pasa por <see cref="IBuyerSessionCache"/> (spec 2026-11-13, D-4): un acierto devuelve una entidad reconstruida
/// (<see cref="BuyerSession.Restore"/>) sin seguimiento de EF, que sólo sirve para leer. Por eso revocar la sesión actual no usa lo
/// que devuelve <see cref="TryResolveAsync"/> sino <see cref="RevokeCurrentAsync"/>, que lee la fila (plan P3).
/// </remarks>
public sealed class BuyerSessions(
    IBuyerSessionRepository sessions,
    IBuyersUnitOfWork unitOfWork,
    IClock clock,
    IBuyerSessionCache cache)
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
    /// <c>LastSeenAt</c> (y guarda) sólo si pasó el umbral. Lo que devuelve puede venir de la caché: sólo se lee.
    /// </summary>
    public async Task<BuyerSession?> TryResolveAsync(string host, string? rawToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(rawToken) || rawToken.Length > TokenMaxLength)
        {
            return null;
        }

        var tokenHash = BuyerTokenCodec.Hash(rawToken);
        var now = clock.UtcNow;

        // Un acierto vuelve a exigir host y vigencia, y una entrada con LastSeenAt de TouchThreshold o más es un fallo de caché:
        // así el Touch sigue escribiendo como antes.
        if (await cache.GetAsync(tokenHash, cancellationToken) is { } cached)
        {
            var restored = BuyerSession.Restore(
                cached.SessionId, cached.AccountId, cached.StoreHost, tokenHash, cached.CreatedAt, cached.LastSeenAt,
                cached.ExpiresAt);
            if (restored.IsValidFor(host, now) && now - restored.LastSeenAt < BuyerSession.TouchThreshold)
            {
                return restored;
            }
        }

        var session = await sessions.FindByTokenHashAsync(tokenHash, cancellationToken);
        // Touch no mira la validez: se pregunta primero. Una sesión inválida no se cachea.
        if (session is null || !session.IsValidFor(host, now))
        {
            return null;
        }

        if (session.Touch(now))
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        // La foto después del posible Touch.
        await cache.SetAsync(
            tokenHash,
            new CachedBuyerSession(
                session.Id, session.AccountId, session.StoreHost, session.CreatedAt, session.LastSeenAt, session.ExpiresAt),
            cancellationToken);
        return session;
    }

    public async Task<BuyerSession> RequireAsync(string host, string? rawToken, CancellationToken cancellationToken) =>
        await TryResolveAsync(host, rawToken, cancellationToken)
        ?? throw new RequestUnauthorizedException(BuyerErrorCodes.SessionInvalid, "The buyer session is not valid.");

    /// <summary>
    /// Revoca la sesión del token en ese host, leída de la base y no de la caché (plan P3), y saca su entrada antes del guardado y
    /// otra vez después. <c>false</c> si no había una sesión válida: no hace nada.
    /// </summary>
    public async Task<bool> RevokeCurrentAsync(
        string host, string? rawToken, string reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(rawToken) || rawToken.Length > TokenMaxLength)
        {
            return false;
        }

        var session = await sessions.FindByTokenHashAsync(BuyerTokenCodec.Hash(rawToken), cancellationToken);
        var now = clock.UtcNow;
        if (session is null || !session.IsValidFor(host, now))
        {
            return false;
        }

        session.Revoke(now, reason);
        RevokedBuyerSession[] revoked = [new(session.Id, session.TokenHash)];
        await cache.RemoveAsync(revoked, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await cache.RemoveAsync(revoked, cancellationToken);
        return true;
    }

    /// <summary>Guarda lo pendiente (el hash nuevo) y revoca toda otra sesión de la cuenta, en la misma transacción.</summary>
    public Task RevokeOthersAsync(
        Guid accountId, Guid keepSessionId, string reason, CancellationToken cancellationToken) =>
        SaveAndRevokeAsync(accountId, keepSessionId, reason, cancellationToken);

    /// <summary>Guarda lo pendiente (el hash nuevo) y revoca toda sesión de la cuenta, en la misma transacción.</summary>
    public Task RevokeAllAsync(Guid accountId, string reason, CancellationToken cancellationToken) =>
        SaveAndRevokeAsync(accountId, null, reason, cancellationToken);

    // T9: primero el guardado (el UPDATE de la cuenta, con su xmin, la bloquea) y después la revocación masiva, que ve toda sesión
    // confirmada hasta ese momento. Revocar una lista leída antes dejaba viva la sesión de un login que confirmaba en el medio.
    // La caché (D-4): los hashes que devolvió la revocación salen dentro de la transacción, antes del commit, y otra vez después.
    private async Task SaveAndRevokeAsync(
        Guid accountId, Guid? keepSessionId, string reason, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        IReadOnlyList<RevokedBuyerSession> revoked = [];
        await unitOfWork.SaveChangesAsync(
            async token =>
            {
                revoked = await sessions.RevokeActiveAsync(accountId, keepSessionId, now, reason, token);
                await cache.RemoveAsync(revoked, token);
            },
            cancellationToken);
        await cache.RemoveAsync(revoked, cancellationToken);
    }
}
