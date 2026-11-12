namespace Modules.Automations.Infrastructure.Persistence;

/// <summary>
/// El candado de conversación del runner (<see cref="AutomationsUnitOfWork.TryLockConversationAsync"/>):
/// <c>pg_advisory_xact_lock(Namespace, hashtext(conversation_id::text))</c>. Público para el borrado de clientes (spec 2026-11-10,
/// <c>Bootstrapper.CustomerDeletionReferences</c>), que lo toma antes de bloquear las conversaciones: el runner lo toma antes de su
/// corrida y, con los dos, escribe la conversación por otra conexión. Quien bloquee la conversación sin pasar antes por este candado
/// puede cerrar un ciclo que Postgres no detecta.
/// </summary>
public static class AutomationsConversationLock
{
    /// <summary>Primera clave del lock de dos claves: la fecha del plan. Cualquier constante sirve mientras ningún otro lock de dos
    /// claves la use (hoy sólo está <c>TenantChangeLock.Namespace</c> = 20261008).</summary>
    public const int Namespace = 20261022;
}
