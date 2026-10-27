using Modules.Customers.Domain;

namespace Modules.Customers.UnitTests;

/// <summary>Spec 2026-10-24 §3: el plan de fusión (qué se llena, qué se libera, qué se rechaza) sin tocar la base.</summary>
public sealed class CustomerMergePlanTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid CityId = Guid.Parse("01900000-0000-7000-8000-000000000010");
    private static readonly ClientClassificationId ClassificationId = new(Guid.Parse("01900000-0000-7000-8000-000000000020"));

    private sealed class PlusNormalizer : IPhoneNumberNormalizer
    {
        public string? ToE164(string? phone, string country) =>
            phone is { Length: > 1 } && phone[0] == '+' ? phone : null;

        public bool IsKnownRegion(string regionCode) => true;

        public string? RegionOf(string? e164) => null;
    }

    private static Customer Complete(
        string? email = "ficha@x.co",
        string? businessName = null,
        string? phone = "310 935 2187",
        string number = "900.123.456-1",
        ClientClassificationId? classification = null) =>
        Customer.Create(
            CustomerId.New(),
            TenantId,
            "CLI08000142",
            "Verde Esencial",
            businessName,
            new CustomerAddressDetails { Name = "Principal", Address = "Calle 10 # 45-12", CityId = CityId },
            new CustomerIdentification { Type = IdentificationType.Nit, Number = number },
            null,
            new CustomerContactInfo
            {
                Phone = phone,
                Email = email,
                Address = "Calle 10 # 45-12",
                Country = Customer.ColombiaCountryCode,
                CityId = CityId,
            },
            new CustomerCommercialInfo { ClassificationId = classification },
            Created);

    private static Customer WhatsApp(string bsuid = "CO.1349120865530274") =>
        Customer.CreateIncomplete(CustomerId.New(), TenantId, "Laura", "+573001234567", "CO", bsuid, Created, new PlusNormalizer());

    private static Customer Instagram(string igsid = "17841400000000001") =>
        Customer.CreateIncompleteFromInstagram(CustomerId.New(), TenantId, "Laura", igsid, "@laura", Created);

    private static Customer Register(string number = "1020") =>
        Customer.CreateFromRegister(
            CustomerId.New(), TenantId, "Laura", new CustomerIdentification { Type = IdentificationType.Cc, Number = number },
            "+573001234567", Created, new PlusNormalizer());

    private static Customer Shopify(string? email = null) =>
        Customer.CreateIncompleteFromExternalSale(CustomerId.New(), TenantId, "Laura", null, email, null, Created, new PlusNormalizer());

    [Fact]
    public void EmptyFieldsAreFilledFromTheFirstMergedThatHasThemAndNothingIsOverwritten()
    {
        var survivor = Complete(email: null, businessName: null);
        var first = Complete(email: null, businessName: "B1", phone: "999");
        var second = Complete(email: "m2@x.co", businessName: "B2", phone: "888");

        var plan = CustomerMergePlan.Create(survivor, [first, second]);
        plan.MarkMerged(Now);
        plan.FillSurvivor(Now);

        Assert.Equal("m2@x.co", survivor.Email);
        Assert.Equal("B1", survivor.BusinessName);
        Assert.Equal("310 935 2187", survivor.Phone);
        Assert.Equal([CustomerMergeField.Email, CustomerMergeField.BusinessName], plan.FilledFields);
    }

    [Fact]
    public void ThePhoneTravelsWithItsE164()
    {
        var survivor = Shopify();
        var merged = WhatsApp();

        var plan = CustomerMergePlan.Create(survivor, [merged]);
        plan.MarkMerged(Now);
        plan.FillSurvivor(Now);

        Assert.Equal("+573001234567", survivor.Phone);
        Assert.Equal("+573001234567", survivor.PhoneE164);
        Assert.Contains(CustomerMergeField.Phone, plan.FilledFields);
    }

    [Fact]
    public void TheBsuidMovesOnlyWhenTheSurvivorHasNone()
    {
        var survivor = WhatsApp("CO.survivor");
        var merged = WhatsApp("CO.merged");

        var plan = CustomerMergePlan.Create(survivor, [merged]);
        plan.MarkMerged(Now);
        plan.FillSurvivor(Now);

        Assert.Equal("CO.survivor", survivor.WhatsAppUserId);
        Assert.Equal("CO.merged", merged.WhatsAppUserId);
        Assert.DoesNotContain(CustomerMergeField.WhatsAppUserId, plan.FilledFields);
    }

    [Fact]
    public void MarkMergedReleasesWhatTheSurvivorTakes()
    {
        var survivor = Shopify();
        var whatsApp = WhatsApp();
        var instagram = Instagram();

        var plan = CustomerMergePlan.Create(survivor, [whatsApp, instagram]);
        plan.MarkMerged(Now);

        Assert.Null(whatsApp.WhatsAppUserId);
        Assert.Null(instagram.InstagramUserId);
        Assert.Null(instagram.InstagramUsername);
        Assert.Null(survivor.WhatsAppUserId);

        plan.FillSurvivor(Now);

        Assert.Equal("CO.1349120865530274", survivor.WhatsAppUserId);
        Assert.Equal("17841400000000001", survivor.InstagramUserId);
        Assert.Equal("laura", survivor.InstagramUsername);
        Assert.Equal(
            [CustomerMergeField.Phone, CustomerMergeField.WhatsAppUserId, CustomerMergeField.InstagramUserId, CustomerMergeField.Country],
            plan.FilledFields);
    }

    [Fact]
    public void AnIncompleteSurvivorTakesTheDocumentOfAnIncompleteMerged()
    {
        var survivor = WhatsApp();
        var merged = Register("1020");

        var plan = CustomerMergePlan.Create(survivor, [merged]);
        plan.MarkMerged(Now);

        Assert.Null(merged.IdentificationType);
        Assert.Null(merged.IdentificationNumber);

        plan.FillSurvivor(Now);

        Assert.Equal(IdentificationType.Cc, survivor.IdentificationType);
        Assert.Equal("1020", survivor.IdentificationNumber);
        Assert.Contains(CustomerMergeField.Identification, plan.FilledFields);
    }

    [Fact]
    public void ACompleteSurvivorNeverTakesDocumentCountryOrAddress()
    {
        var survivor = Complete();
        var merged = Register("1020");

        var plan = CustomerMergePlan.Create(survivor, [merged]);
        plan.MarkMerged(Now);
        plan.FillSurvivor(Now);

        Assert.Equal("900.123.456-1", survivor.IdentificationNumber);
        Assert.Equal(IdentificationType.Cc, merged.IdentificationType);
        Assert.Equal("1020", merged.IdentificationNumber);
        Assert.DoesNotContain(CustomerMergeField.Identification, plan.FilledFields);
        Assert.DoesNotContain(CustomerMergeField.Country, plan.FilledFields);
        Assert.DoesNotContain(CustomerMergeField.Address, plan.FilledFields);
    }

    [Fact]
    public void ACompleteMergedKeepsItsDocument()
    {
        var survivor = Complete(number: "111");
        var merged = Complete(number: "222");

        var plan = CustomerMergePlan.Create(survivor, [merged]);
        plan.MarkMerged(Now);

        Assert.Equal("222", merged.IdentificationNumber);
        Assert.Equal(IdentificationType.Nit, merged.IdentificationType);
    }

    [Fact]
    public void TheClassificationIsNeverFilled()
    {
        var survivor = Complete(classification: null);
        var merged = Complete(classification: ClassificationId);

        var plan = CustomerMergePlan.Create(survivor, [merged]);
        plan.MarkMerged(Now);
        plan.FillSurvivor(Now);

        Assert.Null(survivor.ClassificationId);
        Assert.Equal("CLI08000142", survivor.Cuc);
    }

    [Fact]
    public void TheOptOutIsInheritedWithItsSourceAndDate()
    {
        var optOutAt = new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
        var survivor = Complete();
        var first = Complete();
        var second = Complete();
        second.OptOutOfMarketing(MarketingConsentSource.Button, optOutAt);

        var plan = CustomerMergePlan.Create(survivor, [first, second]);
        plan.MarkMerged(Now);
        plan.FillSurvivor(Now);

        Assert.True(survivor.MarketingOptOut);
        Assert.Equal(MarketingConsentSource.Button, survivor.MarketingOptOutSource);
        Assert.Equal(optOutAt, survivor.MarketingOptOutAt);
        Assert.Contains(CustomerMergeField.MarketingOptOut, plan.FilledFields);
    }

    [Fact]
    public void ASurvivorThatAlreadyOptedOutKeepsItsOwnOptOut()
    {
        var own = new DateTimeOffset(2026, 10, 2, 8, 0, 0, TimeSpan.Zero);
        var survivor = Complete();
        survivor.OptOutOfMarketing(MarketingConsentSource.Manual, own);
        var merged = Complete();
        merged.OptOutOfMarketing(MarketingConsentSource.Meta, Now);

        var plan = CustomerMergePlan.Create(survivor, [merged]);
        plan.MarkMerged(Now);
        plan.FillSurvivor(Now);

        Assert.Equal(MarketingConsentSource.Manual, survivor.MarketingOptOutSource);
        Assert.Equal(own, survivor.MarketingOptOutAt);
        Assert.DoesNotContain(CustomerMergeField.MarketingOptOut, plan.FilledFields);
    }

    [Fact]
    public void FillSurvivorAlwaysBumpsTheVersion()
    {
        var survivor = Complete();
        var merged = Complete();

        var plan = CustomerMergePlan.Create(survivor, [merged]);
        plan.MarkMerged(Now);
        plan.FillSurvivor(Now);

        Assert.Empty(plan.FilledFields);
        Assert.Equal(2, survivor.Version);
        Assert.Equal(Now, survivor.UpdatedAt);
    }

    [Fact]
    public void MarkMergedDeactivatesStampsAndBumps()
    {
        var survivor = Complete();
        var merged = Complete();

        CustomerMergePlan.Create(survivor, [merged]).MarkMerged(Now);

        Assert.False(merged.IsActive);
        Assert.True(merged.IsMerged);
        Assert.Equal(survivor.Id, merged.MergedIntoId);
        Assert.Equal(Now, merged.MergedAt);
        Assert.Equal(2, merged.Version);
        Assert.Equal(Now, merged.UpdatedAt);
    }

    // Revisión final #3: la versión de CADA fusionado sube, no sólo la del primero. Es el token de concurrencia que impide fusionar
    // a C en dos que-quedan a la vez: la segunda fusión manda su UPDATE de C con la versión vieja y EF la rechaza (412).
    [Fact]
    public void MarkMergedBumpsTheVersionOfEveryMerged()
    {
        var survivor = Complete();
        var first = Complete();
        var second = WhatsApp();
        var third = Register();
        var before = new[] { first.Version, second.Version, third.Version };

        CustomerMergePlan.Create(survivor, [first, second, third]).MarkMerged(Now);

        Assert.Equal(before.Select(version => version + 1), new[] { first.Version, second.Version, third.Version });
        Assert.Equal(1, survivor.Version);
    }

    [Fact]
    public void CreateDoesNotWrite()
    {
        var survivor = Complete(email: null);
        var merged = Complete(email: "m@x.co");

        var plan = CustomerMergePlan.Create(survivor, [merged]);

        Assert.Equal([CustomerMergeField.Email], plan.FilledFields);
        Assert.Null(survivor.Email);
        Assert.Equal(1, survivor.Version);
        Assert.Equal(1, merged.Version);
        Assert.True(merged.IsActive);
        Assert.False(merged.IsMerged);
    }

    [Fact]
    public void AnAlreadyMergedSurvivorOrMergedIs422AlreadyMerged()
    {
        var survivor = Complete();
        var merged = Complete();
        CustomerMergePlan.Create(survivor, [merged]).MarkMerged(Now);
        var other = Complete();

        var asSurvivor = Assert.Throws<CustomersDomainException>(() => CustomerMergePlan.Create(merged, [other]));
        var asMerged = Assert.Throws<CustomersDomainException>(() => CustomerMergePlan.Create(other, [merged]));

        Assert.Equal("customers.merge.already_merged", asSurvivor.Code);
        Assert.Equal("customers.merge.already_merged", asMerged.Code);
    }

    [Fact]
    public void AnInactiveSurvivorIs422SurvivorInactiveAndAnInactiveMergedIsFine()
    {
        var inactive = Complete();
        inactive.Deactivate(Now);
        var active = Complete();

        var error = Assert.Throws<CustomersDomainException>(() => CustomerMergePlan.Create(inactive, [active]));
        var plan = CustomerMergePlan.Create(active, [inactive]);

        Assert.Equal("customers.merge.survivor_inactive", error.Code);
        Assert.Single(plan.Merged);
    }

    [Fact]
    public void AnIncompleteSurvivorWithACompleteMergedIs422SurvivorIncomplete()
    {
        var error = Assert.Throws<CustomersDomainException>(() => CustomerMergePlan.Create(WhatsApp(), [Complete()]));

        Assert.Equal("customers.merge.survivor_incomplete", error.Code);
    }

    [Fact]
    public void ARepeatedIdTheSurvivorAmongTheMergedOrAnotherTenantIsAnArgumentError()
    {
        var survivor = Complete();
        var merged = Complete();
        var foreign = Customer.CreateIncompleteFromExternalSale(
            CustomerId.New(), Guid.NewGuid(), "Otro", null, null, null, Created, new PlusNormalizer());

        Assert.Throws<ArgumentException>(() => CustomerMergePlan.Create(survivor, [merged, merged]));
        Assert.Throws<ArgumentException>(() => CustomerMergePlan.Create(survivor, [survivor]));
        Assert.Throws<ArgumentException>(() => CustomerMergePlan.Create(survivor, [foreign]));
    }

    [Fact]
    public void TheMergedKeepTheRequestedOrder()
    {
        var survivor = Complete();
        var first = Complete();
        var second = Complete();

        var plan = CustomerMergePlan.Create(survivor, [second, first]);

        Assert.Equal([second.Id, first.Id], plan.Merged.Select(customer => customer.Id));
    }
}
