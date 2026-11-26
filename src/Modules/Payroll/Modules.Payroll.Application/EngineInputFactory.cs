using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.Application;

/// <summary>
/// De lo que Hr, las capturas y los recurrentes dicen al <see cref="EngineInput"/> que el motor lee. Pura: sin base de datos ni reloj.
/// Nada de lo que Hr tenga programado para después de la quincena entra (R-16b, pre-flight I-10): así un cambio futuro no levanta
/// un <c>sourceChanged</c> falso. El factor de cada hora no se calcula acá: viaja la clave del sistema o el multiplicador propio.
/// </summary>
public static class EngineInputFactory
{
    public static EngineInput Build(
        HrEmployeeSource employee,
        EligibilityResult eligibility,
        PeriodInfo period,
        EngineSettings settings,
        LegalValueSet legal,
        ConceptCatalogSnapshot concepts,
        IReadOnlyList<PeriodInput> inputs,
        IReadOnlyList<RecurringConcept> recurring,
        IReadOnlyDictionary<Guid, decimal> appliedBefore,
        IReadOnlySet<Guid> retiredConcepts,
        MonthAccumulators prior,
        IReadOnlyList<VacationInput> vacations)
    {
        ArgumentNullException.ThrowIfNull(employee);
        ArgumentNullException.ThrowIfNull(eligibility);
        ArgumentNullException.ThrowIfNull(legal);
        ArgumentNullException.ThrowIfNull(concepts);

        var contract = eligibility.Contract
            ?? throw new ArgumentException("Only a liquidable or blocked employee has a single contract.", nameof(eligibility));
        var assignment = eligibility.AssignmentAtEnd
            ?? throw new ArgumentException("The employee has no workplace in the period.", nameof(eligibility));

        var salaries = contract.Salaries
            .Where(salary => salary.EffectiveDate <= period.End)
            .OrderBy(salary => salary.EffectiveDate)
            .Select(salary => new SalarySegmentSource(salary.EffectiveDate, salary.Amount, salary.Teleworker))
            .ToList();

        // A termination after the period is not a fact of this period yet.
        DateOnly? endedOn = contract.EndedOn is { } ended && ended >= period.Start && ended <= period.End ? ended : null;

        var mine = inputs.Where(input => input.EmployeeId == employee.EmployeeId).ToList();

        return new EngineInput(
            employee.EmployeeId,
            period,
            settings,
            legal,
            contract.StartDate,
            endedOn,
            salaries,
            new WorkplaceInfo(
                assignment.WorkplaceName,
                assignment.RiskClass ?? throw new ArgumentException("The workplace has no risk class.", nameof(eligibility)),
                assignment.HighRiskPension),
            Overtime(mine, concepts),
            Incomes(mine, concepts),
            Deductions(mine, concepts),
            RecurringOf(employee.EmployeeId, recurring, appliedBefore, retiredConcepts, concepts),
            prior,
            vacations);
    }

    private static List<OvertimeInput> Overtime(List<PeriodInput> inputs, ConceptCatalogSnapshot concepts) => inputs
        .Where(input => input.Kind == ConceptKind.Overtime)
        .OrderBy(input => input.Position)
        .Select(input =>
        {
            var concept = Require(concepts, input.ConceptId);
            return new OvertimeInput(
                input.ConceptId,
                concept.Name,
                input.Hours ?? 0m,
                concept.System ? concept.SystemKey : null,
                concept.System ? null : concept.Multiplier);
        })
        .ToList();

    private static List<IncomeInput> Incomes(List<PeriodInput> inputs, ConceptCatalogSnapshot concepts) => inputs
        .Where(input => input.Kind == ConceptKind.Income)
        .OrderBy(input => input.Position)
        .Select(input => new IncomeInput(
            input.ConceptId, Require(concepts, input.ConceptId).Name, input.Amount ?? 0m, input.SalaryConstitutive ?? false))
        .ToList();

    private static List<DeductionInput> Deductions(List<PeriodInput> inputs, ConceptCatalogSnapshot concepts) => inputs
        .Where(input => input.Kind == ConceptKind.Deduction)
        .OrderBy(input => input.Position)
        .Select(input => new DeductionInput(input.ConceptId, Require(concepts, input.ConceptId).Name, input.Amount ?? 0m))
        .ToList();

    // Only active, not exhausted ones of a concept that is not retired (decision 5); the order is fixed (creation, then id) so the fingerprint never depends on the database.
    private static List<RecurringInput> RecurringOf(
        Guid employeeId,
        IReadOnlyList<RecurringConcept> recurring,
        IReadOnlyDictionary<Guid, decimal> appliedBefore,
        IReadOnlySet<Guid> retiredConcepts,
        ConceptCatalogSnapshot concepts)
    {
        var result = new List<RecurringInput>();
        foreach (var item in recurring
            .Where(item => item.EmployeeId == employeeId)
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id.Value))
        {
            if (retiredConcepts.Contains(item.ConceptId))
            {
                continue;
            }

            var applied = appliedBefore.GetValueOrDefault(item.Id.Value);
            if (item.StatusFor(applied) != RecurringStatus.Active)
            {
                continue;
            }

            var concept = Require(concepts, item.ConceptId);
            result.Add(new RecurringInput(
                item.Id.Value,
                concept.Name,
                concept.Kind,
                item.Mode,
                item.Amount,
                item.SalaryConstitutive ?? false,
                item.Cap is { } cap ? cap - applied : null));
        }

        return result;
    }

    // A capture or a recurring concept always points at a concept that exists (system or of the company); archiving keeps the row.
    private static ResolvedConcept Require(ConceptCatalogSnapshot concepts, Guid conceptId) =>
        concepts.Find(conceptId) ?? throw new InvalidOperationException($"Concept '{conceptId}' is not in the company catalog.");
}
