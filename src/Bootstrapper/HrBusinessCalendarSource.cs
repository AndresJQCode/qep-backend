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
    public async Task<DateOnly> AddBusinessDaysAsync(Guid tenantId, DateOnly from, int days, CancellationToken cancellationToken)
    {
        var regional = await tenants.GetRegionalSettingsAsync(new TenantId(tenantId), cancellationToken)
            ?? throw new InvalidOperationException($"Tenant '{tenantId}' has no regional settings.");

        return SupportBusinessDays.Add(SupportBusinessCalendar.From(regional.Country), from, days);
    }
}
