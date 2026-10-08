namespace Modules.Tenancy.Infrastructure.Persistence;

/// <summary>
/// Spec 2026-10-08 §3: la clave del <c>pg_advisory_xact_lock</c> sobre el tenant destino. Con prefijo
/// para no compartir espacio con <c>UserLifecycleLockKey</c> (mismo motor de locks, misma base). Pública
/// para que las pruebas de integración tomen el mismo candado.
/// </summary>
public static class TenantChangeLock
{
    public static string KeyFor(Guid tenantId) => $"tenancy.tenant-changes:{tenantId:D}";
}
