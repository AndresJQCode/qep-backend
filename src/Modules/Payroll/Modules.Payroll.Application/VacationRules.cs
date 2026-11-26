using Modules.Payroll.Domain;

namespace Modules.Payroll.Application;

/// <summary>Everything a write or a preview checks about the dates, shared by preview, create and update.</summary>
public sealed record VacationFacts(
    HrEmployeeSource Employee,
    HrContractSource Contract,
    int ComputedBusinessDays,
    DateOnly ReturnDate,
    IReadOnlyList<VacationQuincenaDays> Quincenas,
    IReadOnlyList<string> Months);

public sealed class VacationRules(
    IPayrollHrDirectory hr,
    IPayrollHolidayCalendar holidays,
    IPayrollPeriodRepository periods,
    IPayrollLegalValues legalValues,
    PayrollCompanies companies,
    PayrollEmployees employees)
{
    // Hr guarantees a business day within a week, and VacationBusinessDays.ReturnDate looks at most this far after the end.
    private const int ReturnLookAheadDays = 31;

    /// <summary>
    /// Order: company exists and is active, employee in the company, contract in force on start covers end, first period, then
    /// (Qep only) start is a business day and the employee is liquidable in the paying quincena. With Qep the start is a business
    /// day, so the range has at least one: <c>no_business_days</c> cannot happen and is not checked.
    /// </summary>
    public async Task<VacationFacts> CheckAsync(
        Guid tenantId,
        Guid companyId,
        Guid employeeId,
        VacationOrigin origin,
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken)
    {
        var company = await companies.RequireAsync(tenantId, companyId, cancellationToken);
        PayrollCompanies.EnsureActive(company, "An inactive company cannot change its vacations.");
        await employees.RequireInCompanyAsync(tenantId, companyId, employeeId, cancellationToken);

        // The employee as Hr has it for the range: its contracts that touch it. The one in force on the start day must cover the end.
        var inRange = (await hr.ListEmployeesAsync(tenantId, companyId, start, end, cancellationToken))
            .FirstOrDefault(candidate => candidate.EmployeeId == employeeId);
        var contract = inRange?.Contracts.FirstOrDefault(candidate =>
            candidate.StartDate <= start && (candidate.EndedOn is null || candidate.EndedOn >= start));
        if (inRange is null || contract is null || (contract.EndedOn is { } ended && ended < end))
        {
            throw new PayrollDomainException(
                PayrollErrorCodes.VacationOutsideContract, "The vacation must fit in the contract in force on its first day.");
        }

        // The first quincena of the company is the earliest one QEP has; with none yet the rule does not apply.
        var existing = await periods.ListAsync(tenantId, companyId, cancellationToken);
        if (existing.Count > 0)
        {
            var first = existing.Min(period => period.StartDate);
            var before = origin == VacationOrigin.Qep ? start < first : end < first;
            if (before)
            {
                throw new PayrollDomainException(
                    PayrollErrorCodes.VacationBeforeFirstPeriod,
                    origin == VacationOrigin.Qep
                        ? "The vacation starts before the first quincena in QEP: register it as external."
                        : "The vacation ends before the first quincena in QEP: there is nothing of it to register.");
            }
        }

        var holidaysInRange = await holidays.HolidaysAsync(start, (end.DayNumber > DateOnly.MaxValue.DayNumber - ReturnLookAheadDays ? DateOnly.MaxValue : end.AddDays(ReturnLookAheadDays)), cancellationToken);
        if (origin == VacationOrigin.Qep)
        {
            if (!VacationBusinessDays.IsBusinessDay(start, inRange.RestDays, holidaysInRange))
            {
                throw new PayrollDomainException(
                    PayrollErrorCodes.VacationStartsOnRestDay, "The vacation starts on a rest day or a holiday of the employee.");
            }

            await EnsureLiquidableAsync(tenantId, companyId, employeeId, start, cancellationToken);
        }

        return new VacationFacts(
            inRange,
            contract,
            VacationBusinessDays.Count(start, end, inRange.RestDays, holidaysInRange),
            VacationBusinessDays.ReturnDate(end, inRange.RestDays, holidaysInRange),
            VacationCalendar.PayrollDays(start, end),
            VacationCalendar.Months(start, end));
    }

    // The quincena that pays a Qep vacation must be able to liquidate the employee (an excluded one has no payroll to carry it).
    private async Task EnsureLiquidableAsync(
        Guid tenantId, Guid companyId, Guid employeeId, DateOnly start, CancellationToken cancellationToken)
    {
        var paying = QuincenaCalendar.Containing(start);
        var employee = (await hr.ListEmployeesAsync(tenantId, companyId, paying.Start, paying.End, cancellationToken))
            .FirstOrDefault(candidate => candidate.EmployeeId == employeeId);
        var smmlv = (await legalValues.ResolveAsync(paying.Start, cancellationToken)).Smmlv.Value;
        if (employee is null
            || PayrollEligibility.Evaluate(employee, paying.Start, paying.End, smmlv).Status == EligibilityStatus.Excluded)
        {
            throw new PayrollDomainException(
                PayrollErrorCodes.EmployeeNotLiquidable, "The employee is not liquidable in the quincena that pays the vacation.");
        }
    }
}
