using Microsoft.EntityFrameworkCore;

namespace Bootstrapper;

/// <summary>
/// Plan 2026-11-12 (correcciones), Tarea 4: el candado por tenant de la sincronización de <c>messaging.connection_bots</c>. Lo toman
/// <see cref="AgentConnectionSlots"/> y <see cref="ChatbotConnectionSlots"/> con la misma clave, así que un chatbot y un agente del mismo
/// tenant también hacen fila. El <c>ORDER BY</c> del <c>INSERT</c> no alcanzaba: el <c>DELETE</c> previo toma las filas en cualquier orden y
/// <c>ON CONFLICT DO UPDATE ... WHERE</c> bloquea la fila aunque el <c>WHERE</c> dé falso, así que dos sincronizaciones podían cruzarse en
/// un deadlock.
///
/// Es <c>pg_advisory_xact_lock</c> en la forma de dos claves, <c>(Namespace, hashtext(tenant_id::text))</c>, y se suelta con el commit o el
/// rollback de la transacción del unit of work: por eso exige una transacción abierta (sin ella se soltaría al terminar la sentencia).
/// </summary>
internal static class ConnectionSlotsLock
{
    /// <summary>Primera clave de la forma de dos: la fecha del plan. Cualquier constante sirve mientras ningún otro candado de dos claves
    /// la use (hoy están <c>TenantChangeLock.Namespace</c> = 20261008 y <c>AutomationsConversationLock.Namespace</c> = 20261022).</summary>
    public const int Namespace = 20261112;

    public static Task AcquireAsync(DbContext dbContext, Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({Namespace}, hashtext({tenantId.ToString()}))", cancellationToken);
}
