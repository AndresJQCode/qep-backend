namespace Modules.Tenancy.Application;

/// <summary>
/// Spec 2026-10-08 §1 (O2): QCode es el tenant operador, identificado por configuración. Es el único
/// que conserva los permisos <c>operator.*</c>. No consulta la base: si el id configurado no existe
/// como tenant, la consola simplemente no es alcanzable por nadie.
/// </summary>
public interface IOperatorTenant
{
    /// <summary>true sólo para el tenant configurado en Platform:OperatorTenantId.</summary>
    bool IsOperator(Guid tenantId);
}
