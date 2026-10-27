using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

// Regla NIT de la parte de facturación (spec 2026-10-26): en Colombia una jurídica se identifica
// con NIT; fuera de Colombia no existe esa distinción.
public sealed class BillingPartyFiscalRulesTests
{
    private static QuotationPartyDetails Billing(PartyPersonType? person, PartyIdentificationType? document) => new()
    {
        Name = "Otra S.A.S.",
        IdentificationNumber = "1020304050",
        PersonType = person,
        IdentificationType = document,
    };

    [Fact]
    public void AColombianJuridicaWithACcIsRejected()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            BillingPartyFiscalRules.EnsureConsistent(Billing(PartyPersonType.Juridica, PartyIdentificationType.Cc), "CO"));

        Assert.Equal("quotations.party.person_type_requires_nit", error.Code);
        Assert.Equal(["Parties.Billing.IdentificationType"], error.FieldErrors.Keys);
    }

    [Fact]
    public void ANonColombianTenantMayBillAJuridicaWithACc()
    {
        BillingPartyFiscalRules.EnsureConsistent(Billing(PartyPersonType.Juridica, PartyIdentificationType.Cc), "ES");
    }

    [Fact]
    public void AJuridicaWithoutIdentificationTypeIsNotAConflict()
    {
        BillingPartyFiscalRules.EnsureConsistent(Billing(PartyPersonType.Juridica, null), "CO");
    }

    [Fact]
    public void NoBillingRowIsNotAConflict()
    {
        BillingPartyFiscalRules.EnsureConsistent(null, "CO");
    }
}
