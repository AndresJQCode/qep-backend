using Modules.Hr.Domain;

namespace Modules.Hr.Application;

/// <summary>
/// La regla de nómina de editar y eliminar registros de cargo y lugar (spec 2026-11-26, «Nómina»): un registro rige desde su fecha
/// hasta el día anterior al siguiente, o hasta el fin del contrato (sin fin, abierto). Se rechaza el cambio que alcanza una nómina
/// congelada de la empresa del contrato.
/// </summary>
internal static class AssignmentPayrollGuard
{
    /// <summary>Los días que rige <paramref name="record"/> dentro de <paramref name="contract"/>; <c>To</c> nulo es abierto.</summary>
    public static (DateOnly From, DateOnly? To) RangeOf(EmploymentContract contract, AssignmentRecord record)
    {
        var next = contract.Assignments
            .Where(candidate => candidate.EffectiveDate > record.EffectiveDate)
            .MinBy(candidate => candidate.EffectiveDate);
        return (record.EffectiveDate, next is null ? contract.EndDate : next.EffectiveDate.AddDays(-1));
    }

    /// <summary>
    /// El rango que alcanza una reescritura ya aplicada: desde la menor entre la fecha vieja y la nueva hasta el fin del rango del
    /// registro reescrito. El fin no depende de la fecha, porque el siguiente no cambia.
    /// </summary>
    public static (DateOnly From, DateOnly? To) RewriteRange(DateOnly oldDate, EmploymentContract contract, AssignmentRecord rewritten)
    {
        var range = RangeOf(contract, rewritten);
        return (oldDate < rewritten.EffectiveDate ? oldDate : rewritten.EffectiveDate, range.To);
    }

    public static bool Reaches(IReadOnlyList<FrozenPayrollPeriod> frozen, Guid companyId, DateOnly from, DateOnly? to) =>
        frozen.Any(period => period.CompanyId == companyId
            && period.StartDate <= (to ?? DateOnly.MaxValue)
            && period.EndDate >= from);

    public static void EnsureFree(IReadOnlyList<FrozenPayrollPeriod> frozen, Guid companyId, (DateOnly From, DateOnly? To) range)
    {
        if (Reaches(frozen, companyId, range.From, range.To))
        {
            throw new HrDomainException(
                HrErrorCodes.AssignmentUsedByPayroll,
                "The change reaches days of an approved or closed payroll period; reopen the period first.");
        }
    }
}
