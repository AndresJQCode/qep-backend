using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;
using Modules.Tenancy.Application;

namespace Modules.Payroll.Application;

/// <summary>Un empleado liquidable con todo lo que el motor va a leer y la huella de eso.</summary>
public sealed record PlannedEmployee(HrEmployeeSource Source, EligibilityResult Eligibility, EngineInput Input, string Fingerprint);

/// <param name="Legal">La foto legal del período si ya existe; si no, la que se resuelve a su fecha de inicio.</param>
/// <param name="Fingerprint">La huella del conjunto (R-16): liquidables, excluidos y bloqueados.</param>
/// <param name="Unpaid">Las vacaciones leídas que su quincena pagadora todavía no pagó (spec, regla 5).</param>
/// <param name="PayingStarts">Las quincenas pagadoras cuya foto guardada leyó este plan: la regla 5 pide que una Liquidada siga al día.</param>
public sealed record LiquidationPlan(
    LegalValueSet Legal,
    IReadOnlyList<PlannedEmployee> Liquidable,
    IReadOnlyList<ExcludedEmployeeDto> Excluded,
    IReadOnlyList<BlockedEmployeeDto> Blocked,
    string Fingerprint,
    IReadOnlyList<UnpaidVacation> Unpaid,
    IReadOnlyList<DateOnly> PayingStarts);

/// <summary>Las vacaciones de un empleado tal como quedarían tras una escritura, en lugar de las guardadas.</summary>
public sealed record VacationListOverride(Guid EmployeeId, IReadOnlyList<Vacation> Vacations);

/// <summary>
/// Qué leer distinto de lo guardado, para probar el efecto de una escritura antes de hacerla (el bloqueo por huella): las vacaciones
/// de un empleado y las fotos que una quincena pagadora estaría por escribir, por id de vacaciones.
/// </summary>
public sealed record PlanOverrides(VacationListOverride? Vacations, IReadOnlyDictionary<Guid, VacationPhoto> Photos)
{
    public static PlanOverrides None { get; } = new(null, new Dictionary<Guid, VacationPhoto>());
}

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
    ITenantClock tenantClock,
    IVacationRepository vacations,
    VacationInputBuilder vacationInputs)
{
    public Task<LiquidationPlan> PlanAsync(PayrollPeriod period, CancellationToken cancellationToken) =>
        PlanAsync(period, PlanOverrides.None, cancellationToken);

    public async Task<LiquidationPlan> PlanAsync(PayrollPeriod period, PlanOverrides overrides, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(overrides);

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

        // One read for the whole company: what overlaps the month up to the end of this quincena (the second half counts the first).
        var vacationsByEmployee = (await vacations.ListOverlappingAsync(
                period.TenantId, period.CompanyId, new DateOnly(period.StartDate.Year, period.StartDate.Month, 1), period.EndDate, cancellationToken))
            .GroupBy(item => item.EmployeeId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<Vacation>)group.ToList());
        if (overrides.Vacations is { } replaced)
        {
            vacationsByEmployee[replaced.EmployeeId] = replaced.Vacations;
        }

        var liquidable = new List<PlannedEmployee>();
        var excluded = new List<ExcludedEmployeeDto>();
        var blocked = new List<BlockedEmployeeDto>();
        var reasons = new List<(Guid EmployeeId, string Reason)>();
        var unpaid = new List<UnpaidVacation>();
        var payingStarts = new SortedSet<DateOnly>();

        foreach (var employee in employees)
        {
            var employeeVacations = vacationsByEmployee.GetValueOrDefault(employee.EmployeeId) ?? [];
            var eligibility = PayrollEligibility.WithVacations(
                PayrollEligibility.Evaluate(employee, period.StartDate, period.EndDate, legal.Smmlv.Value), employeeVacations);
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

            var vacationPlan = await vacationInputs.BuildAsync(
                period, employee, eligibility.Contract!, employeeVacations, overrides.Photos, cancellationToken);
            unpaid.AddRange(vacationPlan.Unpaid);
            payingStarts.UnionWith(vacationPlan.PaidElsewhere);

            var input = EngineInputFactory.Build(
                employee, eligibility, info, engineSettings, legal, concepts, captured, own, applied, retired, prior, vacationPlan.Inputs);
            liquidable.Add(new PlannedEmployee(employee, eligibility, input, InputFingerprint.Of(input)));
        }

        return new LiquidationPlan(
            legal,
            liquidable,
            excluded.OrderBy(entry => entry.FullName, StringComparer.CurrentCultureIgnoreCase).ThenBy(entry => entry.EmployeeId).ToList(),
            blocked,
            InputFingerprint.OfPeriod(liquidable.Select(item => (item.Source.EmployeeId, item.Fingerprint)), reasons),
            unpaid,
            [.. payingStarts]);
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
