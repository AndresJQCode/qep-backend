using System.Globalization;
using BuildingBlocks.Application;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application;

/// <param name="Date">
/// <c>yyyy-MM-dd</c> tal como llegó en la URL; nulo o vacío ⇒ hoy en el calendario del tenant. Se lee en el handler y no en el
/// endpoint: así un llamador de otro tenant recibe 403 y no un 422 sobre un dato que todavía no puede ver.
/// </param>
public sealed record GetSalaryRulesQuery(Guid TenantId, string? Date) : IQuery<SalaryRulesDto>;

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

        // Mal formada es un 422 con errors.date: un valor que dejara al binding automático lanzaría una BadHttpRequestException,
        // y ApiExceptionHandler la reduce a 500.
        DateOnly? requested = null;
        if (!string.IsNullOrWhiteSpace(query.Date))
        {
            if (!DateOnly.TryParseExact(query.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                HrFieldErrors.Throw("date", "La fecha no es válida. Usa el formato aaaa-mm-dd.");
            }

            requested = parsed;
        }

        var date = requested
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
