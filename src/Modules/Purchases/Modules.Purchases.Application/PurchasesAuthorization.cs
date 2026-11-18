using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Purchases.Application;

/// <summary>
/// Copia de <c>LoansAuthorization</c>, sin la consulta del módulo: tenant de la ruta distinto del activo, o permiso faltante →
/// 403 <c>authorization.denied</c>, nunca 404 (un 404 diría si la orden de otro tenant existe). Con el módulo <c>purchases</c>
/// apagado, <c>ModuleEntitlementMask</c> ya quitó el permiso antes de llegar acá.
/// </summary>
internal static class PurchasesAuthorization
{
    public static void EnsureAuthorized(IExecutionContext executionContext, Guid tenantId, string permission)
    {
        if (executionContext.TenantId.Value != tenantId || !executionContext.HasPermission(permission))
        {
            throw Denied();
        }
    }

    /// <summary>
    /// La membresía activa de quien escribe: el historial y la orden nombran membresías, así que sin una no hay actor y la
    /// respuesta es 403 <c>authorization.denied</c> (como <c>QuotationAdvisorResolver</c>).
    /// </summary>
    public static async Task<Guid> ResolveMemberAsync(
        IExecutionContext executionContext,
        IMembershipDirectory membershipDirectory,
        Guid tenantId,
        CancellationToken cancellationToken) =>
        await membershipDirectory.FindActiveMembershipIdAsync(executionContext.SubjectId, tenantId, cancellationToken)
            ?? throw Denied();

    public static RequestForbiddenException Denied() =>
        new("authorization.denied", "The subject cannot perform this purchases operation for this tenant.");
}
