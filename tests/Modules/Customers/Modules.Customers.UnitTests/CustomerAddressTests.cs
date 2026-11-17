using Modules.Customers.Domain;

namespace Modules.Customers.UnitTests;

/// <summary>
/// Los dos carriles de ciudad de una direccion de la libreta (spec tienda publica 4.2, opcion B):
/// Colombia lleva el id DIVIPOLA, el resto del mundo lleva la ciudad escrita. Lo anterior al campo
/// pais sigue siendo colombiano.
/// </summary>
public sealed class CustomerAddressTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Now.AddHours(1);

    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid CityId = Guid.Parse("01900000-0000-7000-8000-000000000010");
    private static readonly Guid OtherCityId = Guid.Parse("01900000-0000-7000-8000-000000000011");

    private static Customer CreateCustomer() =>
        Customer.Create(
            CustomerId.New(),
            TenantId,
            "CLI08000142",
            "Verde Esencial S.A.S.",
            null,
            new CustomerAddressDetails { Name = "Verde Esencial S.A.S.", Address = "Calle 10 # 45-12", CityId = CityId },
            new CustomerIdentification { Type = IdentificationType.Nit, Number = "900.123.456-1" },
            null,
            new CustomerContactInfo
            {
                Phone = "310 935 2187",
                Email = "compras@verde.co",
                Address = "Calle 10 # 45-12",
                Country = Customer.ColombiaCountryCode,
                CityId = CityId
            },
            new CustomerCommercialInfo
            {
                ClassificationId = new ClientClassificationId(Guid.Parse("01900000-0000-7000-8000-000000000020")),
                VatSurplus = false
            },
            Now);

    private static CustomerAddressDetails Details(
        string? country = null, Guid? cityId = null, string? cityName = null) =>
        new()
        {
            Name = "Bodega",
            Address = "Carrera 7 # 71-21",
            Country = country!,
            CityId = cityId,
            CityName = cityName
        };

    [Fact]
    public void AColombianAddressNeedsACityIdAndNoCityName()
    {
        var customer = CreateCustomer();

        var missing = Assert.Throws<CustomersDomainException>(() =>
            customer.AddAddress(Details("CO"), false, Later));
        var mixed = Assert.Throws<CustomersDomainException>(() =>
            customer.AddAddress(Details("CO", OtherCityId, "Madrid"), false, Later));
        var empty = Assert.Throws<CustomersDomainException>(() =>
            customer.AddAddress(Details("CO", Guid.Empty), false, Later));

        Assert.Equal("customers.address.city_required", missing.Code);
        Assert.Equal("customers.address.city_lane_invalid", mixed.Code);
        Assert.Equal("customers.address.city_required", empty.Code);
        var added = customer.AddAddress(Details("co", OtherCityId), false, Later);
        Assert.Equal("CO", added.Country);
        Assert.Equal(OtherCityId, added.CityId);
        Assert.Null(added.CityName);
    }

    [Fact]
    public void AForeignAddressNeedsACityNameAndNoCityId()
    {
        var customer = CreateCustomer();

        var withId = Assert.Throws<CustomersDomainException>(() =>
            customer.AddAddress(Details("ES", OtherCityId), false, Later));
        var withoutName = Assert.Throws<CustomersDomainException>(() =>
            customer.AddAddress(Details("ES", cityName: "   "), false, Later));
        var tooLong = Assert.Throws<CustomersDomainException>(() =>
            customer.AddAddress(Details("ES", cityName: new string('x', 121)), false, Later));

        Assert.Equal("customers.address.city_lane_invalid", withId.Code);
        Assert.Equal("customers.address.city_required", withoutName.Code);
        Assert.Equal("customers.address.city_name_too_long", tooLong.Code);
        var added = customer.AddAddress(Details("es", cityName: "  Madrid "), false, Later);
        Assert.Equal("ES", added.Country);
        Assert.Null(added.CityId);
        Assert.Equal("Madrid", added.CityName);
    }

    [Theory]
    [InlineData("E")]
    [InlineData("ESP")]
    [InlineData("E1")]
    public void ACountryThatIsNotTwoLettersIsRejected(string country)
    {
        var customer = CreateCustomer();

        var error = Assert.Throws<CustomersDomainException>(() =>
            customer.AddAddress(Details(country, cityName: "Madrid"), false, Later));

        Assert.Equal("customers.address.country_invalid", error.Code);
    }

    [Fact]
    public void AnAddressWithoutCountryIsColombian()
    {
        var customer = CreateCustomer();
        var details = new CustomerAddressDetails
        {
            Name = "Bodega",
            Address = "Carrera 7 # 71-21",
            CityId = OtherCityId
        };

        var added = customer.AddAddress(details, false, Later);

        Assert.Equal("CO", added.Country);
        Assert.All(customer.Addresses, address => Assert.Equal("CO", address.Country));
    }

    [Fact]
    public void AnAddressMovesBetweenLanesOnUpdate()
    {
        var customer = CreateCustomer();
        var added = customer.AddAddress(Details("CO", OtherCityId), false, Later);

        customer.UpdateAddress(added.Id, Details("ES", cityName: "Madrid"), false, Later);
        Assert.Equal("ES", added.Country);
        Assert.Null(added.CityId);
        Assert.Equal("Madrid", added.CityName);

        customer.UpdateAddress(added.Id, Details("CO", OtherCityId), false, Later);
        Assert.Equal("CO", added.Country);
        Assert.Equal(OtherCityId, added.CityId);
        Assert.Null(added.CityName);
    }

    [Fact]
    public void CorrectCityLeavesAForeignPrincipalAddressAlone()
    {
        var customer = CreateCustomer();
        var foreign = customer.AddAddress(Details("ES", cityName: "Madrid"), true, Later);

        customer.CorrectCity(CityId, OtherCityId, Later);

        Assert.Equal(OtherCityId, customer.CityId);
        Assert.Equal("ES", foreign.Country);
        Assert.Null(foreign.CityId);
        Assert.Equal("Madrid", foreign.CityName);
    }
}
