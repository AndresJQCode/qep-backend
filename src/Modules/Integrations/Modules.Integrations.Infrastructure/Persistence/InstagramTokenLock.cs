namespace Modules.Integrations.Infrastructure.Persistence;

/// <summary>
/// Owner, 2026-11-14 (réplicas múltiples): el espacio del candado de base de datos que serializa el refresco del token de
/// Instagram de una conexión, para que dos réplicas no lo refresquen a la vez. Si Meta invalida el token viejo al refrescar,
/// el perdedor guardaría uno muerto o recibiría un 4xx y marcaría la conexión <c>token_expired</c> sin motivo, y ese camino
/// no pasa por el <c>Version</c> optimista. Va en la forma de dos claves, <c>(Namespace, hashtext(id))</c>; el número no lo
/// usa ningún otro candado de dos claves (<c>TenantChangeLock</c> = 20261008, <c>AutomationsConversationLock</c> = 20261022,
/// <c>ShopifyTokenLock</c> = 20261112, <c>StartupLock</c> = 20261113 de Bootstrapper). Lo toma sólo
/// <c>InstagramTokenRefreshWorker</c>, con <c>pg_try_advisory_xact_lock</c>.
/// </summary>
internal static class InstagramTokenLock
{
    public const int Namespace = 20261114;
}
