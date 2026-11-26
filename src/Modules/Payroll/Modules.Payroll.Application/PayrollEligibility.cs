using Modules.Payroll.Domain;

namespace Modules.Payroll.Application;

public enum EligibilityStatus
{
    Liquidable,
    Excluded,
    Blocked,
}

/// <param name="Contract">El único contrato de la empresa en la quincena; nulo si tiene varios.</param>
/// <param name="SalaryAtEnd">El salario vigente el último día cubierto (R-8); sólo si es liquidable o bloqueado.</param>
/// <param name="AssignmentAtEnd">El cargo y lugar vigente el último día cubierto; sólo si es liquidable o bloqueado.</param>
/// <param name="LastCoveredDay">El último día cubierto: el del período, o el de retiro si el contrato termina adentro.</param>
public sealed record EligibilityResult(
    EligibilityStatus Status,
    ExclusionReason? Exclusion,
    IReadOnlyList<BlockerReason> Blockers,
    HrContractSource? Contract,
    HrSalarySource? SalaryAtEnd,
    HrAssignmentSource? AssignmentAtEnd,
    DateOnly? LastCoveredDay);

/// <summary>
/// A quién se liquida y a quién no (spec «A quién se liquida»). Es pura: decide con lo que Hr devolvió y el SMMLV a la fecha del
/// período. <b>Todas las exclusiones van antes que los bloqueos</b>: un excluido nunca bloquea (pre-flight I-3, R-15).
/// </summary>
public static class PayrollEligibility
{
    public static EligibilityResult Evaluate(
        HrEmployeeSource employee, DateOnly periodStart, DateOnly periodEnd, decimal smmlv)
    {
        ArgumentNullException.ThrowIfNull(employee);
        if (employee.Contracts.Count == 0)
        {
            throw new ArgumentException("An employee in a period has at least one contract of the company.", nameof(employee));
        }

        // 1. R-10: dos o más contratos de la empresa que cubren la quincena. Hr no los deja solaparse; en la práctica es una
        //    terminación y una reincorporación, o una conversión de tipo, dentro de la misma quincena.
        if (employee.Contracts.Count > 1)
        {
            return Excluded(ExclusionReason.MultipleContractsInPeriod, null);
        }

        var contract = employee.Contracts[0];

        // 2. Lo que el motor no sabe calcular todavía.
        if (contract.ContractType != "Employee")
        {
            return Excluded(ExclusionReason.ContractTypeNotSupported, contract);
        }

        if (contract.SalaryType != "Monthly")
        {
            return Excluded(ExclusionReason.SalaryTypeNotSupported, contract);
        }

        if (employee.ContributorSubtype != "Standard")
        {
            return Excluded(ExclusionReason.ContributorSubtypeNotSupported, contract);
        }

        if (employee.ForeignPensionExempt)
        {
            return Excluded(ExclusionReason.ForeignPensionExempt, contract);
        }

        var firstCovered = contract.StartDate > periodStart ? contract.StartDate : periodStart;
        var lastCovered = contract.EndedOn is { } ended && ended < periodEnd ? ended : periodEnd;

        // 3. Un salario por debajo del mínimo que rija dentro de lo cubierto. Cada registro rige hasta el día antes del siguiente.
        var salaries = contract.Salaries.OrderBy(salary => salary.EffectiveDate).ToList();
        for (var index = 0; index < salaries.Count; index++)
        {
            var salary = salaries[index];
            var next = index + 1 < salaries.Count ? salaries[index + 1].EffectiveDate : (DateOnly?)null;
            var coversSomeDay = salary.EffectiveDate <= lastCovered && (next is null || next.Value > firstCovered);
            if (coversSomeDay && salary.Amount < smmlv)
            {
                return Excluded(ExclusionReason.SalaryBelowMinimum, contract);
            }
        }

        // 4. Bloqueos: se juntan todos.
        var salaryAtEnd = salaries.LastOrDefault(salary => salary.EffectiveDate <= lastCovered);
        var assignmentAtEnd = contract.Assignments
            .OrderBy(assignment => assignment.EffectiveDate)
            .LastOrDefault(assignment => assignment.EffectiveDate <= lastCovered);

        var blockers = new List<BlockerReason>(3);
        if (salaryAtEnd is null)
        {
            blockers.Add(BlockerReason.MissingSalary);
        }

        if (assignmentAtEnd is null)
        {
            blockers.Add(BlockerReason.MissingWorkplace);
        }
        else if (assignmentAtEnd.RiskClass is null)
        {
            // Defensivo: Hr exige la clase al crear el centro (I-12).
            blockers.Add(BlockerReason.MissingRiskClass);
        }

        return new EligibilityResult(
            blockers.Count > 0 ? EligibilityStatus.Blocked : EligibilityStatus.Liquidable,
            null,
            blockers,
            contract,
            salaryAtEnd,
            assignmentAtEnd,
            lastCovered);
    }

    /// <summary>
    /// A vacation that straddles a boundary of the contract (it touches the contract but starts before it or ends after it) leaves
    /// days outside it (Hr shortened the contract after the vacation was registered): the employee is blocked until the vacation is
    /// corrected. One that does not touch the contract at all belongs to another contract of the employee (the builder drops it) and
    /// blocks nothing. An excluded employee is returned as is.
    /// </summary>
    public static EligibilityResult WithVacations(EligibilityResult result, IEnumerable<Vacation> employeeVacations)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(employeeVacations);
        if (result.Status == EligibilityStatus.Excluded || result.Contract is not { } contract)
        {
            return result;
        }

        var contractEnd = contract.EndedOn ?? DateOnly.MaxValue;
        var outside = employeeVacations.Any(vacation =>
            vacation.StartDate <= contractEnd
            && vacation.EndDate >= contract.StartDate
            && (vacation.StartDate < contract.StartDate || vacation.EndDate > contractEnd));
        if (!outside)
        {
            return result;
        }

        return result with
        {
            Status = EligibilityStatus.Blocked,
            Blockers = [.. result.Blockers, BlockerReason.VacationOutsideContract],
        };
    }

    private static EligibilityResult Excluded(ExclusionReason reason, HrContractSource? contract) =>
        new(EligibilityStatus.Excluded, reason, [], contract, null, null, null);
}
