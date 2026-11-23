using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;
using Modules.Tenancy.Application;

namespace Modules.Payroll.Application;

/// <summary>Un empleado liquidable con todo lo que el motor va a leer y la huella de eso.</summary>
public sealed record PlannedEmployee(HrEmployeeSource Source, EligibilityResult Eligibility, EngineInput Input, string Fingerprint);

/// <param name="Legal">La foto legal del período si ya existe; si no, la que se resuelve a su fecha de inicio.</param>
/// <param name="Fingerprint">La huella del conjunto (R-16): liquidables, excluidos y bloqueados.</param>
public sealed record LiquidationPlan(
    LegalValueSet Legal,
    IReadOnlyList<PlannedEmployee> Liquidable,
    IReadOnlyList<ExcludedEmployeeDto> Excluded,
    IReadOnlyList<BlockedEmployeeDto> Blocked,
    string Fingerprint);

/// <summary>
/// Lee todo lo que una liquidación necesita (configuración, catálogo, Hr, capturas, recurrentes, la primera quincena) y arma la
/// entrada del motor de cada empleado sin escribir nada: sirve para liquidar y para saber, al leer un período Liquidado, si Hr o la
/// configuración cambiaron desde entonces (<c>sourceChanged</c>).
/// </summary>
public sealed class LiquidationPlanner(
    IPayrollHrDirectory hr,
    IPayrollLegalValues legalValues,
    ICompanyPayrollSettingsRepository settings,
    ConceptCatalog catalog,
    IRecurringConceptRepository recurring,
    RecurringAccumulation accumulation,
    IPayrollResultStore results,
    IPayrollPeriodRepository periods,
    ITenantClock tenantClock)
{
    public async Task<LiquidationPlan> PlanAsync(PayrollPeriod period, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(period);

        var saved = await settings.FindAsync(period.TenantId, period.CompanyId, cancellationToken);
        var engineSettings = saved?.ToEngine() ?? CompanyPayrollSettings.DefaultsFor();
        var concepts = await catalog.LoadAsync(period.TenantId, period.CompanyId, cancellationToken);

        // An archived concept's recurring stop the period after the archive day, counted in the tenant's day (decision 5).
        var calendar = await tenantClock.GetAsync(period.TenantId, cancellationToken);
        var retired = concepts.ArchivedBefore(
            period.StartDate, instant => DateOnly.FromDateTime(calendar.ToLocal(instant).DateTime));

        // The photo of the first liquidation never changes; before it exists the values are the ones in force on the start date.
        var legal = PayrollResultMapper.ParseLegal(period.LegalValuesJson)
            ?? await legalValues.ResolveAsync(period.StartDate, cancellationToken);
        var info = new PeriodInfo(period.StartDate, period.EndDate, period.Half);

        var employees = await hr.ListEmployeesAsync(
            period.TenantId, period.CompanyId, period.StartDate, period.EndDate, cancellationToken);
        var recurringByEmployee = (await recurring.ListByCompanyAsync(period.TenantId, period.CompanyId, cancellationToken))
            .GroupBy(item => item.EmployeeId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<RecurringConcept>)group.ToList());
        var firstHalfResults = await FirstHalfResultsAsync(period, cancellationToken);
        var captured = await periods.ListInputsAsync(period.TenantId, period.Id, cancellationToken);

        var liquidable = new List<PlannedEmployee>();
        var excluded = new List<ExcludedEmployeeDto>();
        var blocked = new List<BlockedEmployeeDto>();
        var reasons = new List<(Guid EmployeeId, string Reason)>();

        foreach (var employee in employees)
        {
            var eligibility = PayrollEligibility.Evaluate(employee, period.StartDate, period.EndDate, legal.Smmlv.Value);
            if (eligibility.Status == EligibilityStatus.Excluded)
            {
                var reason = eligibility.Exclusion!.Value.ToString();
                excluded.Add(new ExcludedEmployeeDto(
                    employee.EmployeeId, employee.DocumentType, employee.DocumentNumber, FullNameOf(employee), reason));
                reasons.Add((employee.EmployeeId, reason));
                continue;
            }

            if (eligibility.Status == EligibilityStatus.Blocked)
            {
                var names = eligibility.Blockers.Select(blocker => blocker.ToString()).ToList();
                blocked.Add(new BlockedEmployeeDto(employee.EmployeeId, names));
                reasons.Add((employee.EmployeeId, "Blocked:" + string.Join(',', names.Order(StringComparer.Ordinal))));
                continue;
            }

            var own = recurringByEmployee.GetValueOrDefault(employee.EmployeeId) ?? [];
            var applied = own.Count == 0
                ? new Dictionary<Guid, decimal>()
                : await accumulation.AccumulatedAsync(
                    period.TenantId, period.CompanyId, employee.EmployeeId, period.StartDate, cancellationToken);
            var prior = firstHalfResults.GetValueOrDefault(employee.EmployeeId)?.Liquidation.Month ?? MonthAccumulators.Empty;

            var input = EngineInputFactory.Build(
                employee, eligibility, info, engineSettings, legal, concepts, captured, own, applied, retired, prior);
            liquidable.Add(new PlannedEmployee(employee, eligibility, input, InputFingerprint.Of(input)));
        }

        return new LiquidationPlan(
            legal,
            liquidable,
            excluded.OrderBy(entry => entry.FullName, StringComparer.CurrentCultureIgnoreCase).ThenBy(entry => entry.EmployeeId).ToList(),
            blocked,
            InputFingerprint.OfPeriod(liquidable.Select(item => (item.Source.EmployeeId, item.Fingerprint)), reasons));
    }

    // The second half reads what the first one withheld and earned in the month; with no first half (or no result for an
    // employee) everything starts at zero.
    private async Task<IReadOnlyDictionary<Guid, EmployeeResult>> FirstHalfResultsAsync(
        PayrollPeriod period, CancellationToken cancellationToken)
    {
        if (period.Half != PayrollHalf.Second)
        {
            return new Dictionary<Guid, EmployeeResult>();
        }

        var first = await periods.FindByStartAsync(
            period.TenantId, period.CompanyId, new DateOnly(period.StartDate.Year, period.StartDate.Month, 1), cancellationToken);
        return first is null
            ? new Dictionary<Guid, EmployeeResult>()
            : (await results.ListAsync(period.TenantId, first.Id, cancellationToken)).ToDictionary(result => result.EmployeeId);
    }

    internal static string FullNameOf(HrEmployeeSource employee) => $"{employee.FirstName} {employee.LastName}".Trim();
}
