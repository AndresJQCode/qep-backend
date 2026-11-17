namespace Modules.Ai.Infrastructure.Persistence;

/// <summary>
/// Revisión de la rama, hallazgo 4 (spec 2026-11-17 copiloto D-C11): el candado de preguntas de un miembro,
/// <c>pg_advisory_xact_lock(Namespace, hashtext(tenant_id || ':' || user_id))</c> en la forma de dos claves. Serializa contar sus preguntas
/// del día y anotar la nueva: sin él, dos <c>POST</c> paralelos pasaban los dos el conteo. Se suelta con el commit o el rollback.
/// <para>El espacio es propio: ninguno de los otros candados de dos claves (<c>TenantChangeLock</c> 20261008, <c>ConversationBotLock</c>
/// 20261022, <c>ShopifyTokenLock</c> 20261112, <c>StartupLock</c> 20261113, <c>InstagramTokenLock</c> 20261114,
/// <c>ConnectionSlotsLock</c> 20261115) lo usa; lo amarra <c>ConnectionSlotsLockTests</c>.</para>
/// </summary>
internal static class CopilotAskLock
{
    public const int Namespace = 20261117;

    public static string KeyFor(Guid tenantId, Guid userId) => $"{tenantId}:{userId}";
}
