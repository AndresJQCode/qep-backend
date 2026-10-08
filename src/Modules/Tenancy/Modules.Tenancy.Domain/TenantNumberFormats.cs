namespace Modules.Tenancy.Domain;

/// <summary>
/// How the frontend groups thousands and marks decimals. Stored as the example itself so the
/// value is self-describing in the database and in the API; the frontend maps it to an Intl locale.
/// </summary>
public static class TenantNumberFormats
{
    public const string CommaDecimal = "1.234,56";
    public const string DotDecimal = "1,234.56";

    public static string Normalize(string value)
    {
        var normalized = value.Trim();
        return normalized is CommaDecimal or DotDecimal
            ? normalized
            : throw new TenantDomainException(
                "tenancy.settings.number_format.invalid",
                $"Number format must be '{CommaDecimal}' or '{DotDecimal}'.");
    }
}
