using BuildingBlocks.Application;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.CallFailures;

/// <summary><c>GET …/siigo/call-failures/{id}</c> (<c>siigo.calls.read</c>).</summary>
public sealed record GetSiigoCallFailureQuery(Guid TenantId, Guid Id) : IQuery<SiigoCallFailureDetailDto>;

/// <summary>
/// Spec 2026-11-21. Tenant, permiso y módulo primero (403, nunca 404); después, un id que no es del tenant (o que ya expiró a los 7 días) es
/// 404 <c>siigo.call_failure.not_found</c>: el tenant de la ruta ya es el del llamador, así que no confirma nada de otro.
/// </summary>
public sealed class GetSiigoCallFailureHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISiigoCallFailureRepository repository) : IQueryHandler<GetSiigoCallFailureQuery, SiigoCallFailureDetailDto>
{
    public async Task<SiigoCallFailureDetailDto> HandleAsync(GetSiigoCallFailureQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await SiigoAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, SiigoPermissions.CallsRead, cancellationToken);

        var failure = await repository.FindAsync(query.TenantId, query.Id, cancellationToken)
            ?? throw new ResourceNotFoundException(SiigoErrorCodes.CallFailureNotFound, "The failed Siigo call was not found.");
        return SiigoCallFailureDetailDto.From(failure);
    }
}
