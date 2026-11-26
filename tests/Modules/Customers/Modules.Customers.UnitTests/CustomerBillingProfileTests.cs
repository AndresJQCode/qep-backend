using Modules.Customers.Domain;
using static Modules.Customers.UnitTests.CustomerDomainFixtures;

namespace Modules.Customers.UnitTests;

/// <summary>
/// La libreta de perfiles de facturación del cliente (spec 2026-11-24 §3.1–3.4, §4): a quién se le factura, con sus
/// retenciones y su excedente de IVA. Gemela de la libreta de direcciones.
/// </summary>
public sealed class CustomerBillingProfileTests
{
    private static readonly DateTimeOffset Later = Now.AddHours(1);
    private static readonly Guid RegionId = Guid.Parse("01900000-0000-7000-8000-000000000030");
    private static readonly Guid RateId = Guid.Parse("01900000-0000-7000-8000-000000000040");
    private static readonly Guid OtherRateId = Guid.Parse("01900000-0000-7000-8000-000000000041");

    private static Customer NewCustomer() => CustomerDomainFixtures.Create(IdentificationType.Nit, null);

    private static CustomerBillingProfileDetails Colombian(
        string name = "Comercial Andina SAS", string number = "900123456", string address = "Cra 43A # 1-50") =>
        new()
        {
            Name = name,
            PersonType = "Juridica",
            IdentificationType = "NIT",
            IdentificationNumber = number,
            Phone = "3001234567",
            Email = "facturas@andina.co",
            Address = address,
            Country = "CO",
            CityId = CityId
        };

    private static CustomerBillingProfileDetails Spanish() =>
        new()
        {
            Name = "Andina Iberia SL",
            IdentificationType = "NIF",
            IdentificationNumber = "B12345678",
            Address = "Calle Mayor 1",
            Country = "ES",
            CityName = "Madrid"
        };

    private static string CodeOf(Action action) => Assert.Throws<CustomersDomainException>(action).Code;

    private static string CodeOfAdding(CustomerBillingProfileDetails details) =>
        CodeOf(() => NewCustomer().EnsureBillingProfile(details, false, Later));

    [Fact]
    public void TheDetailsAreTrimmedUppercasedAndDeduplicated()
    {
        var customer = NewCustomer();

        var (profile, added) = customer.EnsureBillingProfile(
            Colombian() with
            {
                Name = "  Comercial Andina SAS ",
                PersonType = " juridica ",
                IdentificationType = " nit ",
                IdentificationNumber = " 900123456 ",
                Phone = "  ",
                Email = " facturas@andina.co ",
                Address = " Cra 43A # 1-50 ",
                Country = "",
                WithholdingTaxRateIds = [RateId, OtherRateId, RateId],
                VatSurplus = true
            },
            false,
            Later);

        Assert.True(added);
        Assert.Equal("Comercial Andina SAS", profile.Name);
        Assert.Equal("Juridica", profile.PersonType);
        Assert.Equal("NIT", profile.IdentificationType);
        Assert.Equal("900123456", profile.IdentificationNumber);
        Assert.Null(profile.Phone);
        Assert.Equal("facturas@andina.co", profile.Email);
        Assert.Equal("Cra 43A # 1-50", profile.Address);
        Assert.Equal("CO", profile.Country);
        Assert.Equal(CityId, profile.CityId);
        Assert.Equal([RateId, OtherRateId], profile.WithholdingTaxRateIds.Order());
        Assert.True(profile.VatSurplus);
        Assert.Equal(customer.Id, profile.CustomerId);
        Assert.Equal(Later, profile.CreatedAt);
    }

    [Fact]
    public void AColombianProfileWithoutCityIdIsRejected() =>
        Assert.Equal("customers.billing_profile.city_required", CodeOfAdding(Colombian() with { CityId = null }));

    [Fact]
    public void AColombianProfileWithCityNameIsRejected() =>
        Assert.Equal(
            "customers.billing_profile.city_lane_invalid",
            CodeOfAdding(Colombian() with { CityName = "Medellín" }));

    [Fact]
    public void ASpanishProfileWithCityIdIsRejected() =>
        Assert.Equal("customers.billing_profile.city_lane_invalid", CodeOfAdding(Spanish() with { CityId = CityId }));

    [Fact]
    public void ASpanishProfileWithoutCityNameIsRejected() =>
        Assert.Equal("customers.billing_profile.city_required", CodeOfAdding(Spanish() with { CityName = " " }));

    [Fact]
    public void AColombianProfileWithRegionIsRejected() =>
        Assert.Equal(
            "customers.billing_profile.region_lane_invalid",
            CodeOfAdding(Colombian() with { RegionId = RegionId }));

    [Fact]
    public void ASpanishProfileKeepsItsRegion()
    {
        var (profile, _) = NewCustomer().EnsureBillingProfile(Spanish() with { RegionId = RegionId }, false, Later);

        Assert.Equal(RegionId, profile.RegionId);
        Assert.Equal("Madrid", profile.CityName);
        Assert.Null(profile.CityId);
    }

    [Theory]
    [InlineData("", "customers.billing_profile.name_required")]
    [InlineData("   ", "customers.billing_profile.name_required")]
    public void AProfileWithoutNameIsRejected(string name, string code) =>
        Assert.Equal(code, CodeOfAdding(Colombian() with { Name = name }));

    [Fact]
    public void TheTextLimitsAreEnforced()
    {
        Assert.Equal(
            "customers.billing_profile.name_too_long",
            CodeOfAdding(Colombian() with { Name = new string('a', CustomerBillingProfile.NameMaxLength + 1) }));
        Assert.Equal(
            "customers.billing_profile.identification_required",
            CodeOfAdding(Colombian() with { IdentificationNumber = " " }));
        Assert.Equal(
            "customers.billing_profile.identification_too_long",
            CodeOfAdding(Colombian() with
            {
                IdentificationNumber = new string('1', CustomerBillingProfile.IdentificationNumberMaxLength + 1)
            }));
        Assert.Equal(
            "customers.billing_profile.phone_too_long",
            CodeOfAdding(Colombian() with { Phone = new string('1', CustomerBillingProfile.PhoneMaxLength + 1) }));
        Assert.Equal(
            "customers.billing_profile.email_invalid",
            CodeOfAdding(Colombian() with { Email = "no-es-un-correo" }));
        Assert.Equal(
            "customers.billing_profile.email_invalid",
            CodeOfAdding(Colombian() with
            {
                Email = new string('a', CustomerBillingProfile.EmailMaxLength) + "@x.co"
            }));
        Assert.Equal(
            "customers.billing_profile.address_required",
            CodeOfAdding(Colombian() with { Address = " " }));
        Assert.Equal(
            "customers.billing_profile.address_too_long",
            CodeOfAdding(Colombian() with { Address = new string('a', CustomerBillingProfile.AddressMaxLength + 1) }));
        Assert.Equal(
            "customers.billing_profile.country_invalid",
            CodeOfAdding(Colombian() with { Country = "COL" }));
        Assert.Equal(
            "customers.customer.city_name_too_long",
            CodeOfAdding(Spanish() with { CityName = new string('a', CustomerContactInfo.CityNameMaxLength + 1) }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("DNI")]
    public void AnUnknownIdentificationTypeIsRejected(string type) =>
        Assert.Equal(
            "customers.billing_profile.identification_type_invalid",
            CodeOfAdding(Colombian() with { PersonType = null, IdentificationType = type }));

    [Fact]
    public void AnUnknownPersonTypeIsRejected() =>
        Assert.Equal(
            "customers.billing_profile.person_type_invalid",
            CodeOfAdding(Colombian() with { PersonType = "Empresa" }));

    [Fact]
    public void NifInColombiaIsNotAllowed()
    {
        var error = Assert.Throws<CustomersDomainException>(() => NewCustomer().EnsureBillingProfile(
            Colombian() with { PersonType = null, IdentificationType = "NIF" }, false, Later));

        Assert.Equal("customers.billing_profile.document_type_not_allowed", error.Code);
        Assert.Contains("IdentificationType", error.FieldErrors.Keys);
    }

    [Fact]
    public void JuridicaWithCcInColombiaRequiresNit()
    {
        var error = Assert.Throws<CustomersDomainException>(() => NewCustomer().EnsureBillingProfile(
            Colombian() with { IdentificationType = "CC" }, false, Later));

        Assert.Equal("customers.billing_profile.person_type_requires_nit", error.Code);
        Assert.Contains("IdentificationType", error.FieldErrors.Keys);
    }

    [Fact]
    public void JuridicaWithNifInSpainIsAllowed()
    {
        var (profile, added) = NewCustomer().EnsureBillingProfile(
            Spanish() with { PersonType = "Juridica" }, false, Later);

        Assert.True(added);
        Assert.Equal("NIF", profile.IdentificationType);
        Assert.Equal("Juridica", profile.PersonType);
    }

    [Fact]
    public void AnEmptyTaxRateIdIsRejected() =>
        Assert.Equal(
            "customers.customer.withholding_tax_rate_invalid",
            CodeOfAdding(Colombian() with { WithholdingTaxRateIds = [RateId, Guid.Empty] }));

    [Fact]
    public void TheFirstProfileIsPrincipalEvenWhenNotAsked()
    {
        var customer = NewCustomer();
        var version = customer.Version;

        var (first, _) = customer.EnsureBillingProfile(Colombian(), false, Later);
        var (second, _) = customer.EnsureBillingProfile(Spanish(), false, Later);

        Assert.True(first.IsPrincipal);
        Assert.False(second.IsPrincipal);
        Assert.Equal(version + 2, customer.Version);
        Assert.Equal(Later, customer.UpdatedAt);
    }

    [Fact]
    public void MarkingAnotherPrincipalUnmarksThePrevious()
    {
        var customer = NewCustomer();
        var (first, _) = customer.EnsureBillingProfile(Colombian(), false, Later);
        var (second, _) = customer.EnsureBillingProfile(Spanish(), true, Later);

        Assert.False(first.IsPrincipal);
        Assert.True(second.IsPrincipal);
        var version = customer.Version;

        customer.MakeBillingProfilePrincipal(first.Id, Later);

        Assert.True(first.IsPrincipal);
        Assert.False(second.IsPrincipal);
        Assert.Equal(version + 1, customer.Version);

        customer.UpdateBillingProfile(second.Id, Spanish(), true, Later);

        Assert.False(first.IsPrincipal);
        Assert.True(second.IsPrincipal);
    }

    [Fact]
    public void UpdatingWithFalseKeepsThePrincipal()
    {
        var customer = NewCustomer();
        var (profile, _) = customer.EnsureBillingProfile(
            Colombian() with { WithholdingTaxRateIds = [RateId] }, false, Later);
        var version = customer.Version;

        var updated = customer.UpdateBillingProfile(
            profile.Id, Spanish() with { WithholdingTaxRateIds = [OtherRateId] }, false, Later.AddHours(1));

        Assert.Same(profile, updated);
        Assert.True(updated.IsPrincipal);
        Assert.Equal("Andina Iberia SL", updated.Name);
        Assert.Null(updated.PersonType);
        Assert.Null(updated.CityId);
        Assert.Null(updated.Phone);
        Assert.Equal([OtherRateId], updated.WithholdingTaxRateIds);
        Assert.Equal(Later.AddHours(1), updated.UpdatedAt);
        Assert.Equal(version + 1, customer.Version);
    }

    [Fact]
    public void RemovingAProfileTouchesTheCustomer()
    {
        var customer = NewCustomer();
        customer.EnsureBillingProfile(Colombian(), false, Later);
        var (second, _) = customer.EnsureBillingProfile(Spanish(), false, Later);
        var version = customer.Version;

        customer.RemoveBillingProfile(second.Id, Later);

        Assert.Single(customer.BillingProfiles);
        Assert.Equal(version + 1, customer.Version);
    }

    [Fact]
    public void ThePrincipalCannotBeRemoved()
    {
        var customer = NewCustomer();
        var (profile, _) = customer.EnsureBillingProfile(Colombian(), false, Later);

        Assert.Equal(
            "customers.billing_profile.principal_not_removable",
            CodeOf(() => customer.RemoveBillingProfile(profile.Id, Later)));
        Assert.Single(customer.BillingProfiles);
    }

    [Fact]
    public void AMissingProfileIsNotFound()
    {
        var customer = NewCustomer();
        var missing = CustomerBillingProfileId.New();

        Assert.Equal(
            "customers.billing_profile.not_found",
            CodeOf(() => customer.UpdateBillingProfile(missing, Colombian(), false, Later)));
        Assert.Equal(
            "customers.billing_profile.not_found",
            CodeOf(() => customer.RemoveBillingProfile(missing, Later)));
        Assert.Equal(
            "customers.billing_profile.not_found",
            CodeOf(() => customer.MakeBillingProfilePrincipal(missing, Later)));
    }

    [Fact]
    public void EnsuringTheSameNumberWrittenDifferentlyReturnsTheExistingWithoutTouching()
    {
        var customer = NewCustomer();
        var (dotted, _) = customer.EnsureBillingProfile(Colombian(number: "900.123-456"), false, Later);
        var (lettered, _) = customer.EnsureBillingProfile(Spanish() with { IdentificationNumber = "x" }, false, Later);
        var version = customer.Version;

        var plain = customer.EnsureBillingProfile(
            Colombian(name: " comercial  andina sas", number: "900123456", address: "CRA 43A  # 1-50"),
            true,
            Later.AddHours(1));
        var upper = customer.EnsureBillingProfile(
            Spanish() with { IdentificationNumber = "X", CityName = " madrid " }, false, Later.AddHours(1));

        Assert.False(plain.Added);
        Assert.Same(dotted, plain.Profile);
        Assert.False(upper.Added);
        Assert.Same(lettered, upper.Profile);
        Assert.Equal(version, customer.Version);
        Assert.Equal(Later, customer.UpdatedAt);
        Assert.Equal("900.123-456", dotted.IdentificationNumber);
        // Ni siquiera «principal»: un duplicado no cambia nada.
        Assert.True(dotted.IsPrincipal);
    }

    [Fact]
    public void DifferentPhoneTaxesOrVatSurplusAreStillTheSameProfile()
    {
        var customer = NewCustomer();
        var (saved, _) = customer.EnsureBillingProfile(Colombian(), false, Later);
        var version = customer.Version;

        var again = customer.EnsureBillingProfile(
            Colombian() with
            {
                PersonType = null,
                Phone = "3109999999",
                Email = "otro@andina.co",
                WithholdingTaxRateIds = [RateId],
                VatSurplus = true
            },
            false,
            Later);

        Assert.False(again.Added);
        Assert.Same(saved, again.Profile);
        Assert.Equal("3001234567", saved.Phone);
        Assert.Empty(saved.WithholdingTaxRateIds);
        Assert.False(saved.VatSurplus);
        Assert.Equal(version, customer.Version);
    }

    [Fact]
    public void ADifferentAddressAddsAProfile()
    {
        var customer = NewCustomer();
        customer.EnsureBillingProfile(Colombian(), false, Later);

        var otherAddress = customer.EnsureBillingProfile(Colombian(address: "Calle 1 # 2-3"), false, Later);
        var otherName = customer.EnsureBillingProfile(Colombian(name: "Otra SAS"), false, Later);
        var otherNumber = customer.EnsureBillingProfile(Colombian(number: "900123457"), false, Later);
        var otherType = customer.EnsureBillingProfile(
            Colombian() with { PersonType = null, IdentificationType = "CC" }, false, Later);
        var otherCity = customer.EnsureBillingProfile(Colombian() with { CityId = RegionId }, false, Later);
        var spanish = customer.EnsureBillingProfile(Spanish(), false, Later);
        var otherForeignCity = customer.EnsureBillingProfile(Spanish() with { CityName = "Sevilla" }, false, Later);
        var otherCountry = customer.EnsureBillingProfile(
            Spanish() with { Country = "PT", IdentificationType = "PASAPORTE" }, false, Later);

        Assert.All(
            new[] { otherAddress, otherName, otherNumber, otherType, otherCity, spanish, otherForeignCity, otherCountry },
            result => Assert.True(result.Added));
        Assert.Equal(9, customer.BillingProfiles.Count);
    }

    private static Customer WithFiftyProfiles()
    {
        var customer = NewCustomer();
        for (var index = 0; index < CustomerBillingProfile.MaxPerCustomer; index++)
        {
            customer.EnsureBillingProfile(Colombian(number: $"9001{index:D5}"), false, Later);
        }

        return customer;
    }

    [Fact]
    public void TheFiftyFirstDistinctProfileHitsTheLimit()
    {
        var customer = WithFiftyProfiles();

        Assert.Equal(50, CustomerBillingProfile.MaxPerCustomer);
        Assert.Equal(
            "customers.billing_profile.limit_reached",
            CodeOf(() => customer.EnsureBillingProfile(Colombian(number: "800000000"), false, Later)));
        Assert.Equal(50, customer.BillingProfiles.Count);
    }

    [Fact]
    public void ADuplicateWithFiftySavedIsReturned()
    {
        var customer = WithFiftyProfiles();
        var version = customer.Version;

        var again = customer.EnsureBillingProfile(Colombian(number: "9001-00007"), false, Later);

        Assert.False(again.Added);
        Assert.Equal("900100007", again.Profile.IdentificationNumber);
        Assert.Equal(version, customer.Version);
    }

    [Fact]
    public void IncompleteMergedOrInactiveCustomersCannotEnsureAProfile()
    {
        var incomplete = Customer.CreateIncompleteFromExternalSale(
            CustomerId.New(), TenantId, "Comprador", "3001234567", null, "CO", Now, new FakeNormalizer());
        var merged = NewCustomer();
        CustomerMergePlan.Create(NewCustomer(), [merged]).MarkMerged(Now);
        var inactive = NewCustomer();
        inactive.Deactivate(Now);

        Assert.Equal(
            "customers.customer.incomplete",
            CodeOf(() => incomplete.EnsureBillingProfile(Colombian(), false, Later)));
        Assert.Equal(
            "customers.customer.merged",
            CodeOf(() => merged.EnsureBillingProfile(Colombian(), false, Later)));
        Assert.Equal(
            "customers.customer.inactive",
            CodeOf(() => inactive.EnsureBillingProfile(Colombian(), false, Later)));
    }

    // Como la libreta de direcciones: editar, marcar y quitar sólo miran fusionado e inactivo (un incompleto no tiene
    // perfiles que editar).
    [Fact]
    public void MergedOrInactiveCustomersCannotEditTheirProfiles()
    {
        var inactive = NewCustomer();
        var (profile, _) = inactive.EnsureBillingProfile(Colombian(), false, Now);
        inactive.Deactivate(Now);

        Assert.Equal(
            "customers.customer.inactive",
            CodeOf(() => inactive.UpdateBillingProfile(profile.Id, Colombian(), false, Later)));
        Assert.Equal(
            "customers.customer.inactive",
            CodeOf(() => inactive.MakeBillingProfilePrincipal(profile.Id, Later)));
        Assert.Equal(
            "customers.customer.inactive",
            CodeOf(() => inactive.RemoveBillingProfile(profile.Id, Later)));
    }
}
