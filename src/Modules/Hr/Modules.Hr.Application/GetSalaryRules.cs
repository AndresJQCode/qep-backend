using BuildingBlocks.Application;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application;

/// <param name="Date">Nulo ⇒ hoy en el calendario del tenant.</param>
public sealed record GetSalaryRulesQuery(Guid TenantId, DateOnly? Date) : IQuery<SalaryRulesDto>;

/// <summary>Los valores legales vigentes a la fecha, y los tipos de contrato que pueden quedar bajo el mínimo sin confirmar.</summary>
public sealed record SalaryRulesDto(
    DateOnly Date,
    decimal Smmlv,
    decimal IntegralFloor,
    IReadOnlyList<string> BelowMinimumAllowedContractTypes);

public sealed class GetSalaryRulesHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    SalaryRules salaryRules)
    : IQueryHandler<GetSalaryRulesQuery, SalaryRulesDto>
{
    public async Task<SalaryRulesDto> HandleAsync(GetSalaryRulesQuery query, CancellationToken cancellationToken)
    {
        await HrAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken, HrPermissions.ContractRead);

        var date = query.Date
            ?? (await tenantClock.GetAsync(query.TenantId, cancellationToken)).Today;
        var floor = await salaryRules.FloorAtAsync(date, cancellationToken);

        // En el orden del enum, no el de iteración del conjunto: el contrato lo fija el orden de la tabla de etiquetas.
        var allowed = Enum.GetValues<ContractType>()
            .Where(SalaryFloorPolicy.BelowMinimumAllowed.Contains)
            .Select(type => type.ToString())
            .ToList();

        return new SalaryRulesDto(floor.Date, floor.Smmlv, floor.IntegralFloor, allowed);
    }
}
