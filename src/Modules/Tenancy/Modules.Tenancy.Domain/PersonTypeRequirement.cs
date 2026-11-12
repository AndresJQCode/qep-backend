namespace Modules.Tenancy.Domain;

/// <summary>
/// Cuándo exige el tenant que la persona a quien se factura tenga tipo de persona (natural o
/// jurídica). Spec 2026-10-26. Es un ajuste del tenant y manda en todos los países:
/// ver <see cref="PersonTypeRequirementPolicy.Effective"/>.
/// </summary>
public enum PersonTypeRequirement
{
    Never,
    OnCustomerSave,

    /// <summary>Al marcar el pedido como pagado y al facturarlo (spec 2026-11-10 §6). El nombre viene de
    /// cuando era «al aprobar» y no se renombra: es valor persistido y contrato con el frontend.</summary>
    OnOrderApproval
}

/// <summary>
/// El requisito de tipo de persona aplica en todos los países. La regla «jurídica ⇒ NIT» sigue
/// siendo sólo colombiana y vive en el cliente.
/// </summary>
public static class PersonTypeRequirementPolicy
{
    /// <summary>El requisito guardado manda tal cual, sea cual sea el país del tenant. El país se
    /// conserva en la firma para no tocar a quien ya la llama.</summary>
    public static PersonTypeRequirement Effective(string? tenantCountry, PersonTypeRequirement stored) =>
        stored;

    public static PersonTypeRequirement Parse(string value) =>
        Enum.TryParse<PersonTypeRequirement>(value, ignoreCase: false, out var parsed)
        && Enum.IsDefined(parsed)
            ? parsed
            : throw new TenantDomainException(
                "tenancy.settings.person_type_requirement.invalid",
                "Person type requirement must be one of: Never, OnCustomerSave, OnOrderApproval.");
}
