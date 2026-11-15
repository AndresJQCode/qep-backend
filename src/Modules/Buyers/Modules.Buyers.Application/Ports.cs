using Modules.Buyers.Domain;

namespace Modules.Buyers.Application;

public interface IBuyerAccountRepository
{
    Task<BuyerAccount?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken);

    Task<BuyerAccount?> FindAsync(Guid id, CancellationToken cancellationToken);

    void Add(BuyerAccount account);

    void Remove(BuyerAccount account);

    /// <summary>
    /// Bloquea la fila de la cuenta (<c>FOR UPDATE</c>) hasta el fin de la transacción en curso; fuera de una transacción no
    /// retiene nada. Choca con el <c>FOR KEY SHARE</c> que toma la clave foránea al insertar una sesión: un login concurrente o
    /// confirmó antes y se ve, o espera.
    /// </summary>
    Task LockAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>
/// Buyers → Quotations para el alta diferida del checkout (pedidos del comprador §4.2(2)): pone la cuenta en el pedido sólo si el
/// pedido no tiene una. El adaptador vive en Bootstrapper.
/// </summary>
public interface IBuyerOrderLinker
{
    Task LinkAsync(Guid tenantId, Guid orderId, Guid accountId, CancellationToken cancellationToken);
}

public interface IBuyerProfileRepository
{
    Task<BuyerProfile?> FindAsync(Guid accountId, CancellationToken cancellationToken);

    void Add(BuyerProfile profile);

    void Remove(BuyerProfile profile);
}

public interface IBuyerSessionRepository
{
    void Add(BuyerSession session);

    Task<BuyerSession?> FindByTokenHashAsync(string hash, CancellationToken cancellationToken);

    /// <summary>Las sesiones de la cuenta que no están revocadas ni vencidas por su vida absoluta.</summary>
    Task<IReadOnlyList<BuyerSession>> ListActiveAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>
    /// Revoca con una sola sentencia toda sesión sin revocar de la cuenta salvo <paramref name="keepSessionId"/>. A diferencia de
    /// revocar una lista leída antes, alcanza también la sesión que un login concurrente confirmó entre la lectura y el guardado.
    /// Devuelve las que revocó (id y hash): la caché de sesión saca sus entradas.
    /// </summary>
    Task<IReadOnlyList<RevokedBuyerSession>> RevokeActiveAsync(
        Guid accountId, Guid? keepSessionId, DateTimeOffset now, string reason, CancellationToken cancellationToken);
}

/// <summary>
/// La foto de una sesión de comprador válida que guarda la caché (spec 2026-11-13, D-4): lo necesario para reconstruirla con
/// <see cref="BuyerSession.Restore"/> y volver a exigir <see cref="BuyerSession.IsValidFor"/>.
/// </summary>
public sealed record CachedBuyerSession(
    Guid SessionId,
    Guid AccountId,
    string StoreHost,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset ExpiresAt);

/// <summary>Una sesión de comprador revocada o borrada: el id va al log de una falla, el hash es la clave de su entrada.</summary>
public sealed record RevokedBuyerSession(Guid SessionId, string TokenHash);

/// <summary>
/// Caché de las sesiones de comprador (spec 2026-11-13, D-4). La clave es el hash del token, nunca el token crudo. Ninguna
/// operación lanza por un fallo de la caché: la lectura devuelve <c>null</c> y se lee Postgres. Quien revoca llama
/// <see cref="RemoveAsync"/> antes del commit y otra vez después.
/// </summary>
public interface IBuyerSessionCache
{
    /// <returns>La entrada, o <c>null</c> si no está, si la sesión se marcó revocada o si la caché no respondió.</returns>
    Task<CachedBuyerSession?> GetAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>Guarda la foto con TTL <c>min(lo que le queda, 15 min)</c>. Nunca lanza.</summary>
    Task SetAsync(string tokenHash, CachedBuyerSession session, CancellationToken cancellationToken);

    /// <summary>Marca cada sesión como revocada y borra su entrada. Nunca lanza; si falla, lo loguea con el id de cada sesión.</summary>
    Task RemoveAsync(IReadOnlyCollection<RevokedBuyerSession> sessions, CancellationToken cancellationToken);
}

public interface IBuyerEmailTokenRepository
{
    void Add(BuyerEmailToken token);

    /// <summary>Marca como usados los tokens sin usar de la cuenta y del propósito: emitir uno nuevo invalida los anteriores.</summary>
    Task InvalidateUnusedAsync(
        Guid accountId, BuyerEmailTokenPurpose purpose, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Consumo atómico, de un solo uso: una sola sentencia que se confirma sola (precedente D6 de Identity).
    /// Devuelve la cuenta del token, o <c>null</c> si no existe, ya se usó o venció.
    /// </summary>
    Task<Guid?> TryConsumeAsync(
        string tokenHash, BuyerEmailTokenPurpose purpose, DateTimeOffset now, CancellationToken cancellationToken);
}

public interface IBuyerConsentRepository
{
    void Add(BuyerConsent consent);
}

/// <summary>Escribe en el outbox de la plataforma dentro de la unidad de trabajo del llamador.</summary>
public interface IBuyerOutbox
{
    void Add(string eventName, object payload, DateTimeOffset occurredAt);

    /// <summary>
    /// Borra con una sola sentencia las filas de la cuenta de los correos que preceden al borrado (verificación, «ya tienes una
    /// cuenta», restablecer); nunca la de cuenta eliminada. Va en la transacción del borrado de la cuenta.
    /// </summary>
    Task RemoveAccountEmailsAsync(Guid accountId, CancellationToken cancellationToken);
}

public interface IBuyersUnitOfWork
{
    /// <summary>
    /// Confirma los cambios. La violación de <c>IX_accounts_email</c> sale como
    /// <see cref="BuildingBlocks.Application.ResourceConflictException"/> con <see cref="BuyerErrorCodes.EmailTaken"/>. Una cuenta
    /// que cambió o desapareció desde que se leyó sale como <see cref="BuildingBlocks.Application.RequestConcurrencyException"/>.
    /// En los dos casos el tracker queda limpio.
    /// </summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Confirma los cambios y corre <paramref name="afterSave"/> (una sentencia masiva) en la misma transacción. Las mismas
    /// traducciones que <see cref="SaveChangesAsync(CancellationToken)"/>.
    /// </summary>
    Task SaveChangesAsync(Func<CancellationToken, Task> afterSave, CancellationToken cancellationToken);

    /// <summary>
    /// Como <see cref="SaveChangesAsync(Func{CancellationToken, Task}, CancellationToken)"/>, y además corre
    /// <paramref name="beforeSave"/> dentro de la misma transacción, antes del guardado (por ejemplo, un candado y una lectura).
    /// </summary>
    Task SaveChangesAsync(
        Func<CancellationToken, Task> beforeSave, Func<CancellationToken, Task> afterSave, CancellationToken cancellationToken);

    void DiscardPendingChanges();
}

public enum BuyerPasswordCheck
{
    Failed,
    Success,
    SuccessRehashNeeded,
}

public interface IBuyerPasswordHasher
{
    string Hash(string password);

    /// <summary>Con <c>null</c> verifica contra un hash ficticio y devuelve <see cref="BuyerPasswordCheck.Failed"/>.</summary>
    BuyerPasswordCheck Verify(string? hash, string password);
}

/// <summary>La tienda que atiende el host de la ruta: <paramref name="Host"/> normalizado y el nombre que ve el comprador.</summary>
public sealed record BuyerStorefront(string Host, string Name, Guid TenantId);

/// <summary>Resuelve el host de la ruta con las reglas de Store. Un host que no es de una tienda publicada lanza el 404 tal cual.</summary>
public interface IBuyerStorefronts
{
    Task<BuyerStorefront> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary>El desafío anti-bot. <c>false</c> = no se pasó; un proveedor caído lanza el 503 <c>store.captcha.unavailable</c> tal cual.</summary>
public interface IBuyerCaptcha
{
    Task<bool> VerifyAsync(string token, string visitorIp, string host, CancellationToken cancellationToken);
}
