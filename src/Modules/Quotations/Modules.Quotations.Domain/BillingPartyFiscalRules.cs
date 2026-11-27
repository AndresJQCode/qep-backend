namespace Modules.Quotations.Domain;

/// <summary>
/// La regla NIT de la parte de facturación (spec 2026-10-26): en Colombia una persona jurídica se
/// identifica con NIT. Fuera de Colombia no existe esa distinción, así que no aplica.
/// <para>
/// El país que decide es el de la parte de facturación, no el del tenant: un tenant colombiano puede facturarle a una
/// jurídica de Costa Rica, que se identifica con <c>TAX_ID</c> (Costa Rica no admite <c>NIT</c>, <see cref="PartyFiscalTypes"/>).
/// Con el país del tenant esa parte no se podía guardar con ningún documento. Es la misma regla que
/// <c>Customer.EnsurePersonTypeMatches</c> aplica con el país del cliente.
/// </para>
/// </summary>
public static class BillingPartyFiscalRules
{
    public const string BillingIdentificationTypeKey = "Parties.Billing.IdentificationType";

    /// <summary>Rechaza una jurídica con un documento que no es NIT, sólo si la parte es colombiana (país en blanco = Colombia,
    /// como <see cref="QuotationPartyDetails.IsColombian"/>). Una jurídica sin tipo de documento no es un conflicto: eso lo
    /// cubre la exigencia de identificación (<c>identification_required</c>).</summary>
    public static void EnsureConsistent(QuotationPartyDetails? billing)
    {
        if (billing is { IsColombian: true, PersonType: PartyPersonType.Juridica, IdentificationType: { } type }
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
