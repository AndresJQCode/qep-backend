using Modules.Customers.Domain;
using static Modules.Customers.UnitTests.CustomerDomainFixtures;

namespace Modules.Customers.UnitTests;

/// <summary>El tipo de documento tiene que estar permitido para el país del cliente.</summary>
public sealed class CustomerDocumentCountryTests
{
    private const string NotAllowed = "customers.customer.document_type_not_allowed";

    [Fact]
    public void CreateRejectsATypeTheCountryDoesNotAllow()
    {
        var error = Assert.Throws<CustomersDomainException>(() => Create(IdentificationType.Cc, null, Spanish()));

        Assert.Equal(NotAllowed, error.Code);
        Assert.Equal(["IdentificationType"], error.FieldErrors.Keys);
    }

    [Fact]
    public void CreateAcceptsASpanishNif() =>
        Assert.Equal(IdentificationType.Nif, Create(IdentificationType.Nif, null, Spanish()).IdentificationType);

    [Fact]
    public void CompleteRejectsAndLeavesTheCustomerIncomplete()
    {
        var incomplete = Customer.CreateIncomplete(
            CustomerId.New(), TenantId, "Alguien", "+34600111222", "ES", "bsuid-1", Now, new FakeNormalizer());

        var error = Assert.Throws<CustomersDomainException>(() => incomplete.Complete(
            "CLI08000001", "Laura", null, null, Identification(IdentificationType.Cc, "1020304050"),
            null, Spanish(), Commercial(), Now.AddHours(1)));

        Assert.Equal(NotAllowed, error.Code);
        Assert.Equal(["IdentificationType"], error.FieldErrors.Keys);

        Assert.False(incomplete.IsComplete);
        Assert.Null(incomplete.IdentificationType);
    }

    [Fact]
    public void UpdateRejectsAndAssignsNothing()
    {
        var customer = Create(IdentificationType.Cc, null);
        var version = customer.Version;

        var error = Assert.Throws<CustomersDomainException>(() => customer.Update(
            "Otro nombre", null, Identification(IdentificationType.Cc, "1020304050"), null,
            Spanish(), Commercial(), "CLI", Now.AddHours(1)));

        Assert.Equal(NotAllowed, error.Code);
        Assert.Equal(["IdentificationType"], error.FieldErrors.Keys);

        Assert.Equal("Verde Esencial", customer.Name);
        Assert.Equal("CO", customer.Country);
        Assert.Equal(version, customer.Version);
    }

    [Fact]
    public void AColombianLegalEntityWithANifIsACountryProblemFirst() =>
        Assert.Equal(
            NotAllowed,
            Assert.Throws<CustomersDomainException>(() => Create(IdentificationType.Nif, PersonType.Juridica)).Code);

    [Fact]
    public void TheRegisterOnlyTakesColombianTypes() =>
        Assert.Equal(
            NotAllowed,
            Assert.Throws<CustomersDomainException>(() => Customer.CreateFromRegister(
                CustomerId.New(), TenantId, "Laura", Identification(IdentificationType.Nif, "12345678Z"),
                "3001234567", Now, new FakeNormalizer())).Code);

    [Theory]
    [InlineData(IdentificationType.Nit, "CO", true)]
    [InlineData(IdentificationType.Nif, "ES", true)]
    [InlineData(IdentificationType.Nif, "CO", false)]
    [InlineData(IdentificationType.TaxId, "US", true)]
    [InlineData(IdentificationType.Cc, "us", false)]
    [InlineData(IdentificationType.Pasaporte, "MX", true)]
    public void IsAllowedForFollowsTheCountryTable(IdentificationType type, string country, bool expected) =>
        Assert.Equal(expected, type.IsAllowedFor(country));
}
