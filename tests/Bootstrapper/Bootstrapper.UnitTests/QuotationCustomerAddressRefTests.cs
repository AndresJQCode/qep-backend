using System.Reflection;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Quotations.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Customers -> Quotations, libreta de direcciones (Task 1B, R9): una direccion de afuera viaja sin ids de DIVIPOLA, con
/// la ciudad escrita y el pais; una colombiana sigue igual que antes (ids y nombres resueltos).
/// </summary>
public sealed class QuotationCustomerAddressRefTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid CityId = Guid.Parse("01900000-0000-7000-8000-000000000010");
    private static readonly Guid DepartmentId = Guid.Parse("01900000-0000-7000-8000-000000000012");

    private static Customer NewCustomer() =>
        Customer.Create(
            CustomerId.New(),
            TenantId,
            "CLI08000142",
            "Verde Esencial S.A.S.",
            null,
            new CustomerAddressDetails { Name = "Principal", Address = "Calle 10 # 45-12", CityId = CityId },
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
                WithRetention = false,
                VatSurplus = false
            },
            Now);

    // El metodo es privado a proposito: el contrato publico es FindAsync, que necesita el repositorio entero; lo que se
    // prueba aca es solo el mapeo de una fila de la libreta.
    private static QuotationCustomerAddressRef Map(
        CustomerAddress address, IReadOnlyDictionary<Guid, CustomerCityRef> cities)
    {
        var method = typeof(QuotationCustomerLookup).GetMethod(
            "ToAddressRef", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (QuotationCustomerAddressRef)method.Invoke(null, [address, cities])!;
    }

    [Fact]
    public void AForeignAddressTravelsWithoutIdsAndWithItsCityText()
    {
        var customer = NewCustomer();
        var address = customer.AddAddress(
            new CustomerAddressDetails
            {
                Name = "Bodega Madrid", Address = "Calle Mayor 1", Country = "ES", CityName = "Madrid", Phone = "+34600111222"
            },
            false,
            Now);

        var mapped = Map(address, new Dictionary<Guid, CustomerCityRef>());

        Assert.Equal(
            new QuotationCustomerAddressRef(
                address.Id.Value, "Bodega Madrid", "Calle Mayor 1", address.Phone, null, "Madrid", null, null, false, "ES"),
            mapped);
        Assert.Null(mapped.CityId);
        Assert.Null(mapped.DepartmentId);
    }

    [Fact]
    public void AColombianAddressKeepsItsResolvedIdsAndNames()
    {
        var customer = NewCustomer();
        var address = customer.AddAddress(
            new CustomerAddressDetails { Name = "Bodega Bogota", Address = "Carrera 7 # 71-21", CityId = CityId },
            false,
            Now);
        var cities = new Dictionary<Guid, CustomerCityRef>
        {
            [CityId] = new(CityId, "11001", "Bogota", DepartmentId, "11", "Bogota D.C."),
        };

        var mapped = Map(address, cities);

        Assert.Equal(
            new QuotationCustomerAddressRef(
                address.Id.Value, "Bodega Bogota", "Carrera 7 # 71-21", address.Phone,
                CityId, "Bogota", DepartmentId, "Bogota D.C.", false, "CO"),
            mapped);
    }
}
