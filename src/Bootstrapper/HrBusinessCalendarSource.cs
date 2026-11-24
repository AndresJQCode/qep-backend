using Modules.Hr.Application.MedicalExams;
using Modules.Support.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// The exit exam deadline of Hr (spec 2026-11-23 §5.5) counts the same business days as the Support terms: Monday to Friday, minus
/// the Colombian national holidays when the tenant country is <c>CO</c>. It reuses <see cref="SupportBusinessCalendar"/> and
/// <see cref="SupportBusinessDays"/> instead of copying the rule. A tenant without regional settings fails instead of guessing the
/// country: a wrong deadline would go unnoticed.
/// </summary>
internal sealed class HrBusinessCalendarSource(ITenantDirectory tenants) : IHrBusinessCalendar
{
    public async Task<DateOnly> AddBusinessDaysAsync(Guid tenantId, DateOnly from, int days, CancellationToken cancellationToken) =>
        SupportBusinessDays.Add(await CalendarAsync(tenantId, cancellationToken), from, days);

    public async Task<int> CountBusinessDaysAsync(Guid tenantId, DateOnly from, DateOnly through, CancellationToken cancellationToken) =>
        new Counter(await CalendarAsync(tenantId, cancellationToken)).Count(from, through);

    public async Task<IHrBusinessDayCounter> CounterAsync(Guid tenantId, CancellationToken cancellationToken) =>
        new Counter(await CalendarAsync(tenantId, cancellationToken));

    private sealed class Counter(SupportBusinessCalendar calendar) : IHrBusinessDayCounter
    {
        public int Count(DateOnly from, DateOnly through)
        {
            var count = 0;
            for (var day = from.AddDays(1); day <= through; day = day.AddDays(1))
            {
                if (calendar.IsBusinessDay(day))
                {
                    count++;
                }
            }

            return count;
        }
    }

    private async Task<SupportBusinessCalendar> CalendarAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var regional = await tenants.GetRegionalSettingsAsync(new TenantId(tenantId), cancellationToken)
            ?? throw new InvalidOperationException($"Tenant '{tenantId}' has no regional settings.");

        return SupportBusinessCalendar.From(regional.Country);
    }
}
