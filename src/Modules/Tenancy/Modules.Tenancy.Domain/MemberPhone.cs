using System.Text.RegularExpressions;

namespace Modules.Tenancy.Domain;

/// <summary>
/// The optional work mobile of a membership, stored as E.164. Same rule as the support ticket
/// requester (<c>TicketRequesterNormalizer.IsMobile</c>): a leading plus, no zero after it, 8 to 15
/// digits and nothing else, so what is stored is what WhatsApp accepts as a recipient.
/// </summary>
public static partial class MemberPhone
{
    public const int MaxLength = 16;

    /// <summary>Trims the value; blank means "no phone". Anything else must be E.164.</summary>
    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (!IsValid(trimmed))
        {
            throw new TenantDomainException(
                "tenancy.membership.phone_invalid",
                "A membership phone must be in E.164 format, for example +573001234567.");
        }

        return trimmed;
    }

    public static bool IsValid(string value) => E164().IsMatch(value);

    // [0-9], not \d: in .NET \d matches any Unicode decimal digit (Arabic-Indic, Devanagari…), which WhatsApp cannot dial.
    [GeneratedRegex(@"^\+[1-9][0-9]{7,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex E164();
}

/// <summary>
/// Three-state input for the phone in a profile update: absent keeps what is stored (an old client
/// never sends it), present with <c>null</c> clears it, present with text sets it.
/// </summary>
public readonly record struct PhoneChange(bool Provided, string? Value)
{
    public static PhoneChange Keep { get; } = new(false, null);

    public static PhoneChange Set(string? value) => new(true, value);
}

/// <summary>What <see cref="Membership.UpdateProfile(string, int?, PhoneChange, DateTimeOffset)"/> actually changed.</summary>
public readonly record struct MembershipProfileChanges(bool Profile, bool Phone)
{
    public bool Any => Profile || Phone;
}
