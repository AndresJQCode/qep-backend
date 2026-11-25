using System.Globalization;
using Modules.Payroll.Domain;

namespace Modules.Payroll.Application;

/// <summary>
/// Arma el <see cref="VacationDto"/> de lo guardado: los días de nómina por quincena, la quincena que paga con su estado de hoy y lo
/// que se puede hacer. Todo se deriva al leer (no hay columna de estado), con una sola lectura de las quincenas de la empresa.
/// </summary>
public sealed class VacationComposer(IVacationRepository vacations, IPayrollPeriodRepository periods)
{
    private static readonly PayrollPeriodStatus[] FrozenStatuses = [PayrollPeriodStatus.Approved, PayrollPeriodStatus.Closed];

    /// <summary>Las del empleado en la empresa, del inicio más reciente al más viejo (desempate por id).</summary>
    public async Task<IReadOnlyList<VacationDto>> ListAsync(
        Guid tenantId, Guid companyId, Guid employeeId, CancellationToken cancellationToken)
    {
        var all = await vacations.ListByEmployeeAsync(tenantId, companyId, employeeId, cancellationToken);
        var byStart = await PeriodsByStartAsync(tenantId, companyId, cancellationToken);
        return all.Select(vacation => ToDto(vacation, byStart)).ToList();
    }

    /// <summary>Those that touch the period: pay in it or have days in it.</summary>
    public async Task<IReadOnlyList<VacationDto>> TouchingAsync(
        PayrollPeriod period, Guid employeeId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(period);
        var touching = (await vacations.ListOverlappingAsync(
                period.TenantId, period.CompanyId, period.StartDate, period.EndDate, cancellationToken))
            .Where(vacation => vacation.EmployeeId == employeeId)
            .OrderByDescending(vacation => vacation.StartDate)
            .ThenBy(vacation => vacation.Id.Value)
            .ToList();
        if (touching.Count == 0)
        {
            return [];
        }

        var byStart = await PeriodsByStartAsync(period.TenantId, period.CompanyId, cancellationToken);
        return touching.Select(vacation => ToDto(vacation, byStart)).ToList();
    }

    private async Task<IReadOnlyDictionary<DateOnly, PayrollPeriod>> PeriodsByStartAsync(
        Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
        (await periods.ListAsync(tenantId, companyId, cancellationToken)).ToDictionary(period => period.StartDate);

    internal static VacationDto ToDto(Vacation vacation, IReadOnlyDictionary<DateOnly, PayrollPeriod> periodsByStart)
    {
        var paying = VacationReach.PayingQuincena(vacation);
        VacationPaidInDto? paidIn = null;
        if (paying is { } quincena)
        {
            var existing = periodsByStart.GetValueOrDefault(quincena.Start);
            paidIn = new VacationPaidInDto(quincena.Start, quincena.End, existing?.Id.Value, existing?.Status.ToString());
        }

        // A frozen quincena (Approved or Closed) is a snapshot: the vacations it reads cannot change under it.
        var frozen = VacationReach.QuincenaStarts(vacation)
            .Any(start => periodsByStart.GetValueOrDefault(start) is { } period && FrozenStatuses.Contains(period.Status));

        return new VacationDto(
            vacation.Id.Value,
            vacation.EmployeeId,
            vacation.Origin.ToString(),
            vacation.StartDate,
            vacation.EndDate,
            vacation.BusinessDays,
            vacation.ReturnDate,
            vacation.PreviousMonthIbc,
            VacationCalendar.PayrollDays(vacation.StartDate, vacation.EndDate)
                .Select(days => new VacationQuincenaDto(days.Start, days.End, days.Days))
                .ToList(),
            paidIn,
            vacation.ExternalMonths
                .OrderBy(month => month.Month)
                .Select(month => new ExternalVacationMonthDto(
                    month.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    month.VacationPay, month.HealthAdvance, month.PensionAdvance, month.SolidarityFundAdvance))
                .ToList(),
            vacation.Version,
            new VacationCanDto(Update: !frozen, Delete: !frozen));
    }
}
