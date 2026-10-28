namespace Modules.Quotations.Domain;

/// <summary>
/// La regla NIT de la parte de facturación (spec 2026-10-26): en Colombia una persona jurídica se
/// identifica con NIT. Fuera de Colombia no existe esa distinción, así que no aplica.
/// </summary>
public static class BillingPartyFiscalRules
{
    // Misma comparación que PersonTypeRequirementPolicy.IsColombian (Tenancy): ordinal, sin
    // distinguir mayúsculas. Duplicada porque Quotations.Domain no referencia Tenancy.
    private const string ColombiaCountryCode = "CO";

    public const string BillingIdentificationTypeKey = "Parties.Billing.IdentificationType";

    /// <summary>Rechaza una jurídica con un documento que no es NIT, sólo si <paramref name="tenantCountry"/>
    /// es colombiano. Una jurídica sin tipo de documento no es un conflicto: eso lo cubre la
    /// exigencia de identificación (<c>identification_required</c>).</summary>
    public static void EnsureConsistent(QuotationPartyDetails? billing, string? tenantCountry)
    {
        if (!string.Equals(tenantCountry, ColombiaCountryCode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (billing is { PersonType: PartyPersonType.Juridica, IdentificationType: { } type }
            && type != PartyIdentificationType.Nit)
        {
            throw new QuotationsDomainException(
                "quotations.party.person_type_requires_nit",
                "A legal entity is identified by its NIT.",
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    [BillingIdentificationTypeKey] = ["A legal entity is identified by its NIT."]
                });
        }
    }
}
