using Modules.Customers.Domain;

namespace Modules.Customers.UnitTests;

/// <summary>Spec 2026-10-10 §6.2: el cliente incompleto que nace de WhatsApp, cómo se completa y su BSUID
/// (D-A6: un BSUID ya puesto no se pisa).</summary>
public sealed class CustomerCompletenessTests
{
    private const string Bsuid = "CO.1349120865530274";

    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid CityId = Guid.Parse("01900000-0000-7000-8000-000000000010");
    private static readonly ClientClassificationId ClassificationId = new(Guid.Parse("01900000-0000-7000-8000-000000000020"));

    /// <summary>Con «+» devuelve el número tal cual; sin «+» no parsea. Basta para el dominio.</summary>
    private sealed class FakeNormalizer : IPhoneNumberNormalizer
    {
        public string? ToE164(string? phone, string country) =>
            phone is { Length: > 1 } && phone[0] == '+' ? phone : null;

        public bool IsKnownRegion(string regionCode) => regionCode is "CO" or "US";

        public string? RegionOf(string? e164) => e164?.StartsWith("+57", StringComparison.Ordinal) == true ? "CO" : null;
    }

    private static Customer Incomplete(string? phone = "+573001234567", string? country = "CO") =>
        Customer.CreateIncomplete(CustomerId.New(), TenantId, "Laura Pérez", phone, country, Bsuid, Now, new FakeNormalizer());

    private static CustomerContactInfo ColombianContact() =>
        new()
        {
            Phone = "+573001234567",
            Email = "laura@correo.co",
            Address = "Calle 10 # 45-12",
            Country = Customer.ColombiaCountryCode,
            CityId = CityId,
        };

    private static CustomerCommercialInfo Commercial(ClientClassificationId classificationId) =>
        new() { ClassificationId = classificationId, WithRetention = false, VatSurplus = false };

    [Fact]
    public void AnIncompleteCustomerHasOnlyNamePhoneCountryAndBsuid()
    {
        var customer = Incomplete();

        Assert.Equal(CustomerCompleteness.Incomplete, customer.Completeness);
        Assert.False(customer.IsComplete);
        Assert.Equal("Laura Pérez", customer.Name);
        Assert.Equal("+573001234567", customer.Phone);
        Assert.Equal("+573001234567", customer.PhoneE164);
        Assert.Equal("CO", customer.Country);
        Assert.Equal(Bsuid, customer.WhatsAppUserId);
        Assert.Null(customer.Cuc);
        Assert.Null(customer.IdentificationType);
        Assert.Null(customer.IdentificationNumber);
        Assert.Null(customer.Identification);
        Assert.Null(customer.Email);
        Assert.Null(customer.Address);
        Assert.Null(customer.CityId);
        Assert.Null(customer.ClassificationId);
        Assert.Empty(customer.Addresses);
        Assert.True(customer.IsActive);
        Assert.Equal(1, customer.Version);
    }

    // P14: sin país, el E.164 igual sale (el número ya trae «+»).
    [Fact]
    public void WithoutCountryThePhoneIsStillNormalizedAndWithoutPhoneNothingIsInvented()
    {
        var noCountry = Incomplete(country: null);
        var noPhone = Incomplete(phone: null, country: null);

        Assert.Equal("+573001234567", noCountry.PhoneE164);
        Assert.Null(noCountry.Country);
        Assert.Null(noPhone.Phone);
        Assert.Null(noPhone.PhoneE164);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankBsuidIsAProgrammingError(string bsuid) =>
        Assert.Throws<ArgumentException>(() =>
            Customer.CreateIncomplete(CustomerId.New(), TenantId, "Laura", null, null, bsuid, Now, new FakeNormalizer()));

    [Fact]
    public void ABsuidLongerThanTheColumnIsAProgrammingError() =>
        Assert.Throws<ArgumentException>(() =>
            Customer.CreateIncomplete(
                CustomerId.New(), TenantId, "Laura", null, null, "CO." + new string('9', Customer.WhatsAppUserIdMaxLength), Now, new FakeNormalizer()));

    [Fact]
    public void CompletingSetsTheCucTheFieldsAndThePrincipalAddress()
    {
        var customer = Incomplete();

        customer.Complete(
            "CLI08000001",
            "Laura Pérez",
            null,
            new CustomerAddressDetails { Name = "Laura Pérez", Address = "Calle 10 # 45-12", CityId = CityId },
            new CustomerIdentification { Type = IdentificationType.Cc, Number = "1020304050" },
            ColombianContact(),
            Commercial(ClassificationId),
            Now.AddHours(1),
            new FakeNormalizer());

        Assert.True(customer.IsComplete);
        Assert.Equal("CLI08000001", customer.Cuc);
        Assert.Equal(IdentificationType.Cc, customer.IdentificationType);
        Assert.Equal("1020304050", customer.IdentificationNumber);
        Assert.Equal("Calle 10 # 45-12", customer.Address);
        Assert.Equal(CityId, customer.CityId);
        Assert.Equal(ClassificationId, customer.ClassificationId);
        Assert.Equal(Bsuid, customer.WhatsAppUserId);
        Assert.True(Assert.Single(customer.Addresses).IsPrincipal);
        Assert.Equal(2, customer.Version);
        Assert.Equal(Now.AddHours(1), customer.UpdatedAt);
    }

    [Fact]
    public void CompletingWithAMissingFieldLeavesTheCustomerUntouched()
    {
        var customer = Incomplete();

        Assert.Throws<CustomersDomainException>(() => customer.Complete(
            "CLI08000001",
            "Laura Pérez",
            null,
            null,
            new CustomerIdentification { Type = IdentificationType.Cc, Number = "1020304050" },
            ColombianContact(),
            Commercial(new ClientClassificationId(Guid.Empty)),
            Now.AddHours(1),
            new FakeNormalizer()));

        Assert.False(customer.IsComplete);
        Assert.Null(customer.Cuc);
        Assert.Equal(1, customer.Version);
    }

    // P13: Update es sólo para la ficha completa; el handler enruta a Complete.
    [Fact]
    public void UpdatingAnIncompleteCustomerIsAProgrammingError()
    {
        var customer = Incomplete();

        Assert.Throws<InvalidOperationException>(() => customer.Update(
            "Laura",
            null,
            new CustomerIdentification { Type = IdentificationType.Cc, Number = "1020304050" },
            ColombianContact(),
            Commercial(ClassificationId),
            "CLI",
            Now));
    }

    // D-A6 y P4: un BSUID ya puesto no se pisa, y poner uno no sube la versión.
    [Fact]
    public void AttachingABsuidOnlyWorksWhenThereIsNone()
    {
        var customer = Incomplete();

        Assert.False(customer.AttachWhatsAppUserId("US.999"));
        Assert.Equal(Bsuid, customer.WhatsAppUserId);
        Assert.Equal(1, customer.Version);
    }

    [Fact]
    public void ReplacingTheBsuidNeedsThePreviousOne()
    {
        var customer = Incomplete();

        Assert.False(customer.ReplaceWhatsAppUserId("CO.otro", "CO.nuevo"));
        Assert.Equal(Bsuid, customer.WhatsAppUserId);
        Assert.True(customer.ReplaceWhatsAppUserId(Bsuid, "CO.nuevo"));
        Assert.Equal("CO.nuevo", customer.WhatsAppUserId);
        Assert.Equal(1, customer.Version);
    }
}
