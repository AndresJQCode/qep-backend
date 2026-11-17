using System.Security.Cryptography;
using BuildingBlocks.Application;
using Microsoft.Extensions.Options;
using Modules.Identity.Application;
using Modules.Identity.Domain;

namespace Modules.Identity.Infrastructure.Persistence;

internal sealed class SessionService(
    ISessionRepository sessionRepository,
    IIdentityUnitOfWork unitOfWork,
    IIdentityAuditRecorder auditRecorder,
    IClock clock,
    IOptions<QepSessionOptions> options,
    ISessionCache cache)
    : ISessionService
{
    // El seguimiento de inactividad sólo es realmente preciso a unos minutos; tocarlo en cada
    // request convertiría toda llamada autenticada en una escritura.
    private static readonly TimeSpan TouchThreshold = TimeSpan.FromMinutes(5);

    public async Task<SessionIssueResult> IssueAsync(
        Guid userId,
        string? userAgent,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        var rawToken = GenerateRawToken();
        var now = clock.UtcNow;
        var session = Session.Issue(
            SessionId.New(),
            new UserId(userId),
            Hash(rawToken),
            now,
            TimeSpan.FromDays(options.Value.AbsoluteLifetimeDays),
            userAgent,
            ipAddress);

        sessionRepository.Add(session);
        auditRecorder.Record(
            userId,
            "identity.session.issued",
            "session",
            session.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new SessionIssueResult(rawToken, session.ExpiresAt);
    }

    public async Task<SessionPrincipal?> ValidateAsync(
        string rawToken,
        CancellationToken cancellationToken)
    {
        var tokenHash = Hash(rawToken);
        var now = clock.UtcNow;

        // Spec 2026-11-13, D-4: un acierto no toca la base. Una entrada con LastSeenAt de más de TouchThreshold es un fallo de
        // caché, para que el seguimiento de inactividad siga escribiendo como antes; sin respuesta de la caché, Postgres.
        var cached = await cache.GetAsync(tokenHash, cancellationToken);
        if (cached is not null && now < cached.ValidUntil && now - cached.LastSeenAt <= TouchThreshold)
        {
            return new SessionPrincipal(cached.UserId);
        }

        var session = await sessionRepository.FindByTokenHashAsync(tokenHash, cancellationToken);
        var idleTimeout = TimeSpan.FromDays(options.Value.IdleTimeoutDays);
        if (session is null || !session.IsValid(now, idleTimeout))
        {
            // Una sesión inválida no se cachea: la entrada sólo existe para una que valía.
            return null;
        }

        if (now - session.LastSeenAt > TouchThreshold)
        {
            session.Touch(now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        // La foto después del posible Touch: el LastSeenAt guardado es el que decide el próximo acierto.
        var lastSeenAt = session.LastSeenAt;
        var validUntil = session.ExpiresAt < lastSeenAt + idleTimeout ? session.ExpiresAt : lastSeenAt + idleTimeout;
        await cache.SetAsync(
            tokenHash,
            new CachedSession(session.Id.Value, session.UserId.Value, lastSeenAt, validUntil),
            cancellationToken);
        return new SessionPrincipal(session.UserId.Value);
    }

    public async Task RevokeAsync(
        string rawToken,
        string reason,
        CancellationToken cancellationToken)
    {
        var session = await sessionRepository.FindByTokenHashAsync(
            Hash(rawToken),
            cancellationToken);
        if (session is null)
        {
            return;
        }

        var now = clock.UtcNow;
        session.Revoke(now, reason);
        auditRecorder.Record(
            session.UserId.Value,
            "identity.session.revoked",
            "session",
            session.Id.ToString(),
            "success",
            now);

        // Antes del guardado y otra vez después (spec 2026-11-13, D-4): la marca de revocación cubre al request que leyó
        // Postgres en el medio.
        RevokedSession[] revoked = [new(session.Id.Value, session.TokenHash)];
        await cache.RemoveAsync(revoked, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await cache.RemoveAsync(revoked, cancellationToken);
    }

    public async Task<int> RevokeAllForUserAsync(
        Guid userId,
        string reason,
        CancellationToken cancellationToken)
    {
        var sessions = await sessionRepository.ListActiveByUserAsync(userId, cancellationToken);
        var now = clock.UtcNow;
        foreach (var session in sessions)
        {
            session.Revoke(now, reason);
            auditRecorder.Record(
                userId,
                "identity.session.revoked",
                "session",
                session.Id.ToString(),
                "success",
                now,
                Modules.Audit.Domain.AuditActorType.System);
        }

        if (sessions.Count > 0)
        {
            var revoked = sessions.Select(session => new RevokedSession(session.Id.Value, session.TokenHash)).ToArray();
            await cache.RemoveAsync(revoked, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await cache.RemoveAsync(revoked, cancellationToken);
        }

        return sessions.Count;
    }

    // 256 bits de entropía, codificados en base64url para que el valor crudo sea seguro en una
    // cookie. El token crudo se devuelve al llamador exactamente una vez (al emitirlo) y nunca
    // se persiste — sólo se guarda su hash (ver Hash).
    private static string GenerateRawToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    // SHA-256 alcanza acá (a diferencia del hashing de contraseñas) porque la entrada ya
    // es un valor aleatorio de alta entropía, no un secreto de baja entropía que un atacante
    // pudiera romper por fuerza bruta desde el hash.
    private static string Hash(string rawToken) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken)));
}
