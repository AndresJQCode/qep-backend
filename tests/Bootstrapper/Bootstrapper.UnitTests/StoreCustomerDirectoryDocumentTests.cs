using Modules.Customers.Application;
using Modules.Customers.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Customers → Store. Un cliente cuyo documento es un NIF español se encuentra por documento: la llave del candidato
/// acepta los siete códigos, no sólo los cuatro colombianos.
/// </summary>
public sealed class StoreCustomerDirectoryDocumentTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Customer Spaniard() =>
        Customer.Create(
            CustomerId.New(), TenantId, "CLI00000142", "Lucía Gómez", null, null,
            new CustomerIdentification { Type = IdentificationType.Nif, Number = "12345678Z" }, null,
            new CustomerContactInfo
            {
                Phone = "+34 600 111 222", Address = "Calle Gran Via 28", Country = "ES", CityName = "Madrid"
            },
            new CustomerCommercialInfo(), Now);

    [Fact]
    public async Task ACandidateWithANifIsFoundByItsDocument()
    {
        var customer = Spaniard();
        var customers = new FakeCustomerRepository(customer);
        customers.Identifications[(IdentificationType.Nif, "12345678Z")] = customer.Id;
        var directory = new StoreCustomerDirectory(customers, new FakeCustomerProvisioning());

        var found = await directory.FindCandidatesAsync(TenantId, "+34600111222", "nif", " 12345678Z ", Ct);

        var candidate = Assert.Single(found);
        Assert.Equal(customer.Id.Value, candidate.Id);
        Assert.Equal("NIF", candidate.IdentificationType);
        Assert.Equal(["Document"], candidate.MatchedBy);
    }

    [Fact]
    public async Task AKnownKeyWithANifMatchesByDocument()
    {
        var customer = Spaniard();
        var customers = new FakeCustomerRepository(customer);
        customers.Identifications[(IdentificationType.Nif, "12345678Z")] = customer.Id;
        var directory = new StoreCustomerDirectory(customers, new FakeCustomerProvisioning());

        var known = await directory.KnownAsync(TenantId, [("+34600111222", "NIF", "12345678Z"), ("+34600333444", "NIF", "99999999R")], Ct);

        Assert.Equal([0], known.ToArray());
    }
}
