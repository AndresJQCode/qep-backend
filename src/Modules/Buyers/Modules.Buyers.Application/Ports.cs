using Modules.Buyers.Domain;

namespace Modules.Buyers.Application;

public interface IBuyerAccountRepository
{
    Task<BuyerAccount?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken);

    Task<BuyerAccount?> FindAsync(Guid id, CancellationToken cancellationToken);

    void Add(BuyerAccount account);

    void Remove(BuyerAccount account);
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
    /// </summary>
    Task RevokeActiveAsync(
        Guid accountId, Guid? keepSessionId, DateTimeOffset now, string reason, CancellationToken cancellationToken);
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
