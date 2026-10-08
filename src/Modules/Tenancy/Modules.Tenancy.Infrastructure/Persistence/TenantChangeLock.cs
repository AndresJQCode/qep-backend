namespace Modules.Tenancy.Infrastructure.Persistence;

/// <summary>
/// Spec 2026-10-08 §3: la clave del <c>pg_advisory_xact_lock</c> sobre el tenant destino. Se toma con la
/// forma de dos claves, <c>(Namespace, hashtext(KeyFor(id)))</c>, que en PostgreSQL es un espacio aparte del
/// de una sola clave: ahí viven <c>UserLifecycleLockKey</c> (<c>hashtext</c> de un correo) y el candado de
/// ventas de POS, así que ningún hash de ellos puede chocar con este. Pública para que las pruebas de
/// integración tomen el mismo candado.
/// </summary>
public static class TenantChangeLock
{
    /// <summary>Primera clave de la forma de dos: la fecha del spec. Cualquier constante sirve mientras
    /// ningún otro candado de dos claves la use (hoy no hay otro).</summary>
    public const int Namespace = 20261008;

    public static string KeyFor(Guid tenantId) => $"tenancy.tenant-changes:{tenantId:D}";
}
