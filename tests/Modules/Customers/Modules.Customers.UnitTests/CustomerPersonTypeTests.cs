using Modules.Customers.Domain;

namespace Modules.Customers.UnitTests;

/// <summary>
/// Spec 2026-10-26: el tipo de persona del cliente y la regla «jurídica colombiana sólo con NIT».
/// </summary>
public sealed class CustomerPersonTypeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid CityId = Guid.Parse("01900000-0000-7000-8000-000000000010");

    private static readonly ClientClassificationId ClassificationId =
        new(Guid.Parse("01900000-0000-7000-8000-000000000020"));

    private static CustomerIdentification Identification(IdentificationType type, string number = "900123456") =>
        new() { Type = type, Number = number };

    private static CustomerContactInfo Colombian() =>
        new()
        {
            Phone = "310 935 2187",
            Email = "compras@verde.co",
            Address = "Calle 10 # 45-12",
            Country = "CO",
            CityId = CityId
        };

    private static CustomerContactInfo Spanish() =>
        new()
        {
            Phone = "+34 910 000 000",
            Email = "compras@verde.es",
            Address = "Calle Gran Via 28",
            Country = "ES",
            CityName = "Madrid"
        };

    private static CustomerCommercialInfo Commercial() => new() { ClassificationId = ClassificationId };

    private static Customer Create(
        IdentificationType document,
        PersonType? personType,
        CustomerContactInfo? contact = null) =>
        Customer.Create(
            CustomerId.New(), TenantId, "CLI08000142", "Verde Esencial", null, null,
            Identification(document), personType, contact ?? Colombian(), Commercial(), Now);

    private static CustomersDomainException Fails(Action action) =>
        Assert.Throws<CustomersDomainException>(action);

    [Theory]
    [InlineData(PersonType.Natural, IdentificationType.Cc)]
    [InlineData(PersonType.Natural, IdentificationType.Nit)]
    [InlineData(PersonType.Juridica, IdentificationType.Nit)]
    public void CreateKeepsAConsistentPersonType(PersonType type, IdentificationType document)
    {
        var customer = Create(document, type);

        Assert.Equal(type, customer.PersonType);
    }

    [Fact]
    public void CreateWithoutPersonTypeIsAllowed()
    {
        var customer = Create(IdentificationType.Cc, null);

        Assert.Null(customer.PersonType);
    }

    [Theory]
    [InlineData(IdentificationType.Cc)]
    [InlineData(IdentificationType.Ce)]
    [InlineData(IdentificationType.Pasaporte)]
    public void AColombianJuridicaWithoutNitIsRejectedOnCreate(IdentificationType document)
    {
        var exception = Fails(() => Create(document, PersonType.Juridica));

        Assert.Equal("customers.customer.person_type_requires_nit", exception.Code);
        Assert.Equal(["IdentificationType"], exception.FieldErrors.Keys);
    }

    [Fact]
    public void AForeignJuridicaCustomerMayKeepACc()
    {
        var customer = Create(IdentificationType.Cc, PersonType.Juridica, Spanish());

        Assert.Equal(PersonType.Juridica, customer.PersonType);
    }

    [Fact]
    public void UpdateWithJuridicaAndCcThrowsAndLeavesTheCustomerUntouched()
    {
        var customer = Create(IdentificationType.Nit, PersonType.Natural);
        var version = customer.Version;

        var exception = Fails(() => customer.Update(
            "Otro nombre", null, Identification(IdentificationType.Cc, "1020304050"), PersonType.Juridica,
            Colombian(), Commercial(), "CLI", Now.AddHours(1)));

        Assert.Equal("customers.customer.person_type_requires_nit", exception.Code);
        Assert.Equal("Verde Esencial", customer.Name);
        Assert.Equal(IdentificationType.Nit, customer.IdentificationType);
        Assert.Equal(PersonType.Natural, customer.PersonType);
        Assert.Equal(version, customer.Version);
    }

    [Fact]
    public void UpdateWithNullClearsThePersonType()
    {
        var customer = Create(IdentificationType.Nit, PersonType.Juridica);

        customer.Update(
            "Verde Esencial", null, Identification(IdentificationType.Nit), null,
            Colombian(), Commercial(), "CLI", Now.AddHours(1));

        Assert.Null(customer.PersonType);
    }

    [Fact]
    public void CompletingAnIncompleteCustomerAppliesTheRule()
    {
        var incomplete = Customer.CreateIncomplete(
            CustomerId.New(), TenantId, "Alguien", "3000000001", "CO", "bsuid-1", Now, new FakeNormalizer());

        Fails(() => incomplete.Complete(
            "CLI08000001", "Laura", null, null, Identification(IdentificationType.Cc, "1020304050"),
            PersonType.Juridica, Colombian(), Commercial(), Now.AddHours(1)));

        Assert.False(incomplete.IsComplete);

        incomplete.Complete(
            "CLI08000001", "Laura", null, null, Identification(IdentificationType.Nit),
            PersonType.Juridica, Colombian(), Commercial(), Now.AddHours(1));

        Assert.True(incomplete.IsComplete);
        Assert.Equal(PersonType.Juridica, incomplete.PersonType);
    }

    [Theory]
    [InlineData("Natural", PersonType.Natural)]
    [InlineData("JURIDICA", PersonType.Juridica)]
    [InlineData(" ", null)]
    [InlineData(null, null)]
    public void ParserReadsTheWireValue(string? wire, PersonType? expected) =>
        Assert.Equal(expected, PersonTypeParser.ParseOptional(wire));

    [Fact]
    public void ParserRejectsAnUnknownValue()
    {
        var exception = Fails(() => PersonTypeParser.ParseOptional("Jurídica"));

        Assert.Equal("customers.customer.person_type_invalid", exception.Code);
    }

    [Fact]
    public void WireValuesHaveNoAccent()
    {
        Assert.Equal("Natural", PersonType.Natural.ToWireValue());
        Assert.Equal("Juridica", PersonType.Juridica.ToWireValue());
    }

    private sealed class FakeNormalizer : IPhoneNumberNormalizer
    {
        public string? ToE164(string? phone, string country) => phone is null ? null : "+57" + phone;

        public bool IsKnownRegion(string regionCode) => regionCode == "CO";

        public string? RegionOf(string? e164) => null;
    }
}
