using Modules.Customers.Domain;
using Modules.Hr.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// <see cref="IMemberPhoneDirectory"/>: the HR phone of a member, only when the tenant has the <c>hr</c> module on. HR keeps the
/// phone as free text, so each one goes through the shared normalizer with the tenant country and must end up as E.164; what does
/// not normalize with certainty is left out, and the notice falls back to email instead of dialing a guess.
/// </summary>
internal sealed class MemberPhoneDirectory(
    ITenantModules modules,
    ITenantDirectory tenants,
    IHrMemberPhones hr,
    IPhoneNumberNormalizer phones) : IMemberPhoneDirectory
{
    public async Task<IReadOnlyDictionary<Guid, string>> FindHrPhonesAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> memberIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, string>();
        if (memberIds.Count == 0)
        {
            return result;
        }

        var enabled = await modules.FindAsync(tenantId, cancellationToken);
        if (enabled is null || !enabled.IsEnabled(TenantModuleKeys.Hr))
        {
            return result;
        }

        var regional = await tenants.GetRegionalSettingsAsync(new TenantId(tenantId), cancellationToken);
        if (regional is null)
        {
            return result;
        }

        foreach (var row in await hr.ListAsync(tenantId, memberIds, cancellationToken))
        {
            var e164 = phones.ToE164(row.Phone, regional.Country);
            if (e164 is not null && MemberPhone.IsValid(e164))
            {
                result[row.MemberId] = e164;
            }
        }

        return result;
    }
}
