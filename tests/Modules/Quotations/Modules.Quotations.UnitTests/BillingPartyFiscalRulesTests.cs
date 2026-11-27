using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

// Regla NIT de la parte de facturación (spec 2026-10-26): en Colombia una jurídica se identifica
// con NIT; fuera de Colombia no existe esa distinción. El país que manda es el de la parte (en blanco
// = Colombia), no el del tenant: una parte de Costa Rica factura con TAX_ID aunque el tenant sea colombiano.
public sealed class BillingPartyFiscalRulesTests
{
    private static QuotationPartyDetails Billing(PartyPersonType? person, PartyIdentificationType? document, string? country = null) => new()
    {
        Name = "Otra S.A.S.",
        IdentificationNumber = "1020304050",
        PersonType = person,
        IdentificationType = document,
        Country = country,
    };

    [Fact]
    public void AColombianJuridicaWithACcIsRejected()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            BillingPartyFiscalRules.EnsureConsistent(Billing(PartyPersonType.Juridica, PartyIdentificationType.Cc, "CO")));

        Assert.Equal("quotations.party.person_type_requires_nit", error.Code);
        Assert.Equal(["Parties.Billing.IdentificationType"], error.FieldErrors.Keys);
    }

    [Fact]
    public void AJuridicaWithoutCountryIsColombianAndNeedsTheNit()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            BillingPartyFiscalRules.EnsureConsistent(Billing(PartyPersonType.Juridica, PartyIdentificationType.Cc, "  ")));

        Assert.Equal("quotations.party.person_type_requires_nit", error.Code);
    }

    [Fact]
    public void ALowercaseColombianCountryStillNeedsTheNit()
    {
        Assert.Throws<QuotationsDomainException>(() =>
            BillingPartyFiscalRules.EnsureConsistent(Billing(PartyPersonType.Juridica, PartyIdentificationType.Cc, "co")));
    }

    [Fact]
    public void AForeignJuridicaMayBillWithATaxId()
    {
        BillingPartyFiscalRules.EnsureConsistent(Billing(PartyPersonType.Juridica, PartyIdentificationType.TaxId, "CR"));
    }

    [Fact]
    public void AColombianJuridicaWithTheNitIsValid()
    {
        BillingPartyFiscalRules.EnsureConsistent(Billing(PartyPersonType.Juridica, PartyIdentificationType.Nit, "CO"));
    }

    [Fact]
    public void AJuridicaWithoutIdentificationTypeIsNotAConflict()
    {
        BillingPartyFiscalRules.EnsureConsistent(Billing(PartyPersonType.Juridica, null, "CO"));
    }

    [Fact]
    public void NoBillingRowIsNotAConflict()
    {
        BillingPartyFiscalRules.EnsureConsistent(null);
    }
}
