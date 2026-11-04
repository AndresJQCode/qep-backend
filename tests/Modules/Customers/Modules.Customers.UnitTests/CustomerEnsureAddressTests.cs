using Modules.Customers.Domain;

namespace Modules.Customers.UnitTests;

/// <summary>
/// Tarea 12 de la tienda pública: «Tomar» busca cada dirección de la solicitud en la libreta del cliente antes de agregarla.
/// La igualdad es por contenido (país, dirección normalizada, ciudad por carril, teléfono E.164), nunca por el nombre.
/// </summary>
public sealed class CustomerEnsureAddressTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Now.AddHours(1);

    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid CityId = Guid.Parse("01900000-0000-7000-8000-000000000010");
    private static readonly Guid OtherCityId = Guid.Parse("01900000-0000-7000-8000-000000000011");

    // Un normalizador de mentira: deja los dígitos y le pone +57 a lo que no trae «+».
    private static string? ToE164(string? phone)
    {
        var digits = new string((phone ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length == 0)
        {
            return null;
        }

        return phone!.TrimStart().StartsWith('+') ? "+" + digits : "+57" + digits;
    }

    private static Customer ColombianCustomer(string addressPhone) =>
        Customer.Create(
            CustomerId.New(),
            TenantId,
            "CLI08000142",
            "Verde Esencial S.A.S.",
            null,
            new CustomerAddressDetails { Name = "Principal", Address = "Cra 7  # 12-30", CityId = CityId, Phone = addressPhone },
            new CustomerIdentification { Type = IdentificationType.Nit, Number = "900.123.456-1" },
            null,
            new CustomerContactInfo
            {
                Phone = "310 935 2187",
                Email = "compras@verde.co",
                Address = "Cra 7  # 12-30",
                Country = Customer.ColombiaCountryCode,
                CityId = CityId
            },
            new CustomerCommercialInfo
            {
                ClassificationId = new ClientClassificationId(Guid.Parse("01900000-0000-7000-8000-000000000020")),
                WithRetention = false,
                VatSurplus = false
            },
            Now);

    // Un cliente de afuera nace sin libreta (Complete/Create sólo siembra la dirección con ciudad de DIVIPOLA).
    private static Customer ForeignCustomer() =>
        Customer.Create(
            CustomerId.New(),
            TenantId,
            "CLI00000143",
            "Casa Verde SL",
            null,
            null,
            new CustomerIdentification { Type = IdentificationType.Pasaporte, Number = "X1234567" },
            null,
            new CustomerContactInfo
            {
                Phone = "+34 600 111 222",
                Email = "hola@casaverde.es",
                Address = "Calle Mayor 1",
                Country = "ES",
                CityName = "Madrid"
            },
            new CustomerCommercialInfo { ClassificationId = null, WithRetention = false, VatSurplus = false },
            Now);

    private static CustomerAddressDetails Details(
        string address, string? phone, string country = "CO", Guid? cityId = null, string? cityName = null) =>
        new()
        {
            Name = "Quien recibe",
            Address = address,
            Country = country,
            CityId = country == "CO" ? cityId ?? CityId : null,
            CityName = cityName,
            Phone = phone
        };

    [Fact]
    public void TheSameAddressIgnoringCaseSpacesAndPhoneFormatIsReused()
    {
        var customer = ColombianCustomer("300 123 4567");
        var existing = Assert.Single(customer.Addresses);

        var (address, added) = customer.EnsureAddress(Details("CRA 7 # 12-30", "+573001234567"), ToE164, Later);

        Assert.False(added);
        Assert.Same(existing, address);
        Assert.Single(customer.Addresses);
    }

    [Fact]
    public void ADifferentCityIsANewAddress()
    {
        var customer = ColombianCustomer("300 123 4567");

        var (address, added) = customer.EnsureAddress(
            Details("Cra 7 # 12-30", "3001234567", cityId: OtherCityId), ToE164, Later);

        Assert.True(added);
        Assert.Equal(OtherCityId, address.CityId);
        Assert.Equal(2, customer.Addresses.Count);
    }

    [Fact]
    public void ADifferentPhoneIsANewAddressAndTwoMissingPhonesMatch()
    {
        var customer = ColombianCustomer("300 123 4567");

        var other = customer.EnsureAddress(Details("Cra 7 # 12-30", "3109999999"), ToE164, Later);
        var noPhone = customer.EnsureAddress(Details("Calle 1 # 2-3", null), ToE164, Later);
        var noPhoneAgain = customer.EnsureAddress(Details("calle 1 # 2-3", "  "), ToE164, Later);

        Assert.True(other.Added);
        Assert.True(noPhone.Added);
        Assert.False(noPhoneAgain.Added);
        Assert.Same(noPhone.Address, noPhoneAgain.Address);
    }

    [Fact]
    public void AForeignAddressMatchesByCityText()
    {
        var customer = ForeignCustomer();
        var first = customer.EnsureAddress(Details("Calle Mayor 1", "+34600111222", "ES", cityName: "madrid "), ToE164, Later);

        var again = customer.EnsureAddress(Details("calle  mayor 1", "+34 600 111 222", "ES", cityName: "Madrid"), ToE164, Later);
        var otherCity = customer.EnsureAddress(Details("Calle Mayor 1", "+34600111222", "ES", cityName: "Sevilla"), ToE164, Later);
        var otherCountry = customer.EnsureAddress(Details("Calle Mayor 1", "+34600111222", "PT", cityName: "Madrid"), ToE164, Later);

        Assert.True(first.Added);
        Assert.False(again.Added);
        Assert.Same(first.Address, again.Address);
        Assert.True(otherCity.Added);
        Assert.True(otherCountry.Added);
        Assert.Equal(3, customer.Addresses.Count);
    }

    [Fact]
    public void AnEnsuredAddressIsNeverPrincipalUnlessItIsTheFirst()
    {
        var withBook = ColombianCustomer("300 123 4567");
        var principal = Assert.Single(withBook.Addresses);
        var added = withBook.EnsureAddress(Details("Calle 1 # 2-3", "3001234567"), ToE164, Later);

        var empty = ForeignCustomer();
        var first = empty.EnsureAddress(Details("Calle Mayor 1", "+34600111222", "ES", cityName: "Madrid"), ToE164, Later);
        var second = empty.EnsureAddress(Details("Calle Menor 2", "+34600111222", "ES", cityName: "Madrid"), ToE164, Later);

        Assert.False(added.Address.IsPrincipal);
        Assert.True(principal.IsPrincipal);
        Assert.True(first.Address.IsPrincipal);
        Assert.False(second.Address.IsPrincipal);
    }

    [Fact]
    public void AnIncompleteCustomerCannotEnsureAnAddress()
    {
        var incomplete = Customer.CreateIncompleteFromExternalSale(
            CustomerId.New(), TenantId, "Comprador", "3001234567", null, "CO", Now, new NoNormalizer());

        var error = Assert.Throws<CustomersDomainException>(() =>
            incomplete.EnsureAddress(Details("Calle 1 # 2-3", "3001234567"), ToE164, Later));

        Assert.Equal("customers.customer.incomplete", error.Code);
    }

    private sealed class NoNormalizer : IPhoneNumberNormalizer
    {
        public string? ToE164(string? phone, string country) => null;

        public bool IsKnownRegion(string regionCode) => true;

        public string? RegionOf(string? e164) => null;
    }
}
