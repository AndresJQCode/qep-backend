namespace Modules.Tenancy.Domain;

/// <summary>
/// Cuándo exige el tenant que la persona a quien se factura tenga tipo de persona (natural o
/// jurídica). Spec 2026-10-26. Es un ajuste del tenant, pero sólo manda si el tenant es
/// colombiano: ver <see cref="PersonTypeRequirementPolicy.Effective"/>.
/// </summary>
public enum PersonTypeRequirement
{
    Never,
    OnCustomerSave,
    OnOrderApproval
}

/// <summary>
/// La regla del país de a quien se factura vive en una sola comparación (Global Constraints del
/// plan): <c>"CO"</c>, ordinal, sin distinguir mayúsculas. Quotations.Domain no ve Tenancy y lleva
/// su propia copia de esa comparación.
/// </summary>
public static class PersonTypeRequirementPolicy
{
    public const string ColombiaCountryCode = "CO";

    public static bool IsColombian(string? country) =>
        string.Equals(country, ColombiaCountryCode, StringComparison.OrdinalIgnoreCase);

    /// <summary>El requisito guardado sólo cuenta si el tenant es colombiano; fuera de Colombia no
    /// existe la distinción natural/jurídica que la DIAN exige, así que es <c>Never</c>.</summary>
    public static PersonTypeRequirement Effective(string? tenantCountry, PersonTypeRequirement stored) =>
        IsColombian(tenantCountry) ? stored : PersonTypeRequirement.Never;

    public static PersonTypeRequirement Parse(string value) =>
        Enum.TryParse<PersonTypeRequirement>(value, ignoreCase: false, out var parsed)
        && Enum.IsDefined(parsed)
            ? parsed
            : throw new TenantDomainException(
                "tenancy.settings.person_type_requirement.invalid",
                "Person type requirement must be one of: Never, OnCustomerSave, OnOrderApproval.");
}
