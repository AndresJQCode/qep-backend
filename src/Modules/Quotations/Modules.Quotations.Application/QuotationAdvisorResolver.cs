using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Traduce el subject autenticado a su <see cref="MemberId"/> (§1.4 del modelo de datos:
/// "asesora"/"usuario" siempre refieren a <c>members</c>, no a <c>identity.users</c>). La
/// política del endpoint ya garantizó que el subject tiene una membresía activa con el permiso
/// pedido; esta llamada sólo resuelve ese id. Compartido por todos los handlers de escritura del
/// módulo — cada uno necesita un <see cref="MemberId"/> para <c>advisor_id</c>/<c>created_by</c>/
/// <c>updated_by</c>/<c>member_id</c>.
/// </summary>
internal static class QuotationAdvisorResolver
{
    public static async Task<MemberId> ResolveAsync(
        IMembershipDirectory membershipDirectory,
        IExecutionContext executionContext,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var membershipId = await membershipDirectory.FindActiveMembershipIdAsync(
            executionContext.SubjectId, tenantId, cancellationToken);
        return membershipId is { } value
            ? new MemberId(value)
            : throw new RequestForbiddenException(
                "authorization.denied",
                "The subject does not have an active membership in this tenant.");
    }
}
