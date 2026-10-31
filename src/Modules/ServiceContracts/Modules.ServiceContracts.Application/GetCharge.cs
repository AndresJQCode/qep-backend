using BuildingBlocks.Application;
using Modules.ServiceContracts.Domain;
using Modules.Tenancy.Application;

namespace Modules.ServiceContracts.Application;

public sealed record GetChargeQuery(Guid TenantId, Guid ChargeId) : IQuery<ChargeDto>;

/// <summary>El detalle de un cobro; el armado es el mismo de la bandeja (<see cref="ChargeDtoComposer"/>), así que un cobro leído coincide con el de la lista.</summary>
public sealed class GetChargeHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IChargeRepository charges,
    IServiceContractRepository services,
    ChargeDtoComposer composer)
    : IQueryHandler<GetChargeQuery, ChargeDto>
{
    public async Task<ChargeDto> HandleAsync(GetChargeQuery query, CancellationToken cancellationToken)
    {
        await ServiceContractsAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, ServiceContractsPermissions.Read, cancellationToken);

        // Acotado al tenant de la ruta: el cobro de otro tenant no es «no tuyo», es «no existe».
        var charge = await charges.FindAsync(query.TenantId, query.ChargeId, cancellationToken)
            ?? throw ServiceContractsNotFound.Charge(query.ChargeId);
        var today = (await tenantClock.GetAsync(query.TenantId, cancellationToken)).Today;

        return await ChargeComposition.ComposeOneAsync(query.TenantId, charge, services, composer, today, cancellationToken);
    }
}

internal static class ChargeComposition
{
    /// <summary>Un solo cobro como <see cref="ChargeDto"/>: carga su servicio acotado al tenant y delega en el armador por lote.</summary>
    public static async Task<ChargeDto> ComposeOneAsync(
        Guid tenantId,
        Charge charge,
        IServiceContractRepository services,
        ChargeDtoComposer composer,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var service = await services.FindAsync(tenantId, charge.ServiceId, cancellationToken)
            ?? throw ServiceContractsNotFound.Service(charge.ServiceId.Value);

        return (await composer.ComposeAsync(
            tenantId, [charge], new Dictionary<ServiceContractId, ServiceContract> { [service.Id] = service }, today, cancellationToken))[0];
    }
}
