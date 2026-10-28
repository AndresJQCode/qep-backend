using Modules.Pos.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

/// <summary>
/// Alcance por cajero (spec, «Autorización»): sin pos.register.read se ven sólo las ventas y cajas
/// propias, como ReportingPermissions.AllAdvisorsRead. pos.register.read amplía; no reemplaza.
/// </summary>
internal static class PosScope
{
    public static async Task<MemberId?> CashierFilterAsync(
        IMembershipDirectory membershipDirectory, IExecutionContext executionContext, Guid tenantId,
        CancellationToken cancellationToken) =>
        executionContext.HasPermission(PosPermissions.RegisterRead)
            ? null
            : await PosCashierResolver.ResolveAsync(membershipDirectory, executionContext, tenantId, cancellationToken);

    public static async Task EnsureCanSeeAsync(
        MemberId owner, IMembershipDirectory membershipDirectory, IExecutionContext executionContext,
        Guid tenantId, CancellationToken cancellationToken)
    {
        if (await CashierFilterAsync(membershipDirectory, executionContext, tenantId, cancellationToken) is { } caller
            && caller != owner)
        {
            throw PosAuthorization.Denied();
        }
    }
}
