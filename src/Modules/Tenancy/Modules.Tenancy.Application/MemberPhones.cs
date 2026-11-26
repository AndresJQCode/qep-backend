using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public enum MemberPhoneSource
{
    Member,
    Hr,
}

/// <summary>The phone a notice would use for a member and where it comes from; the SPA shows it, it never deduces it.</summary>
public sealed record EffectivePhone(string? Phone, MemberPhoneSource? Source)
{
    public static EffectivePhone None { get; } = new(null, null);

    public string? SourceName => Source?.ToString();
}

/// <summary>
/// Narrow port to the HR phone fallback, implemented in Bootstrapper so Tenancy never references HR.
/// </summary>
public interface IMemberPhoneDirectory
{
    /// <summary>
    /// HR phones already normalized to E.164 for the tenant country, keyed by membership id. A
    /// phone that cannot be normalized with certainty is absent: a number is never guessed.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> FindHrPhonesAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> memberIds,
        CancellationToken cancellationToken);
}

/// <summary>
/// The effective phone is resolved on read, never copied: <c>PhoneE164 ?? hr</c>. The own number wins,
/// and clearing it brings the HR one back. HR is only asked about members without their own number.
/// </summary>
public sealed class MembershipPhoneReader(IMemberPhoneDirectory directory)
{
    public async Task<EffectivePhone> ForAsync(Membership membership, CancellationToken cancellationToken)
    {
        var phones = await ForAsync([membership], cancellationToken);
        return phones[membership.Id];
    }

    public async Task<IReadOnlyDictionary<MembershipId, EffectivePhone>> ForAsync(
        IReadOnlyList<Membership> memberships,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<MembershipId, EffectivePhone>();
        var withoutOwn = new List<Membership>();
        foreach (var membership in memberships)
        {
            if (membership.PhoneE164 is { } own)
            {
                result[membership.Id] = new EffectivePhone(own, MemberPhoneSource.Member);
            }
            else
            {
                withoutOwn.Add(membership);
            }
        }

        // One call per tenant keeps the port honest even if a listing ever mixed tenants by mistake.
        foreach (var group in withoutOwn.GroupBy(membership => membership.TenantId))
        {
            var ids = group.Select(membership => membership.Id.Value).ToArray();
            var hr = await directory.FindHrPhonesAsync(group.Key.Value, ids, cancellationToken);
            foreach (var membership in group)
            {
                result[membership.Id] = hr.TryGetValue(membership.Id.Value, out var phone)
                    ? new EffectivePhone(phone, MemberPhoneSource.Hr)
                    : EffectivePhone.None;
            }
        }

        return result;
    }
}
