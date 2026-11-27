using BuildingBlocks.Domain.Identification;
using Modules.Customers.Application;

namespace Modules.Customers.UnitTests;

/// <summary>La regla «número de documento solo con dígitos» del validador de perfiles de facturación.</summary>
public sealed class CustomerBillingProfileWriteRulesTests
{
    private static readonly CustomerBillingProfileWriteRules Rules = new();

    private static AddCustomerBillingProfileCommand Command(string type, string number) =>
        new(CustomerWriteDoubles.Tenant, Guid.NewGuid(), "Comercial Andina SAS", null, type, number, null, null,
            "Cra 43A # 1-50", "CO", CustomerWriteDoubles.CityId, null, null, null, false, false);

    [Fact]
    public void ANitWithLettersIsRejectedOnTheNumber()
    {
        var error = Assert.Single(Rules.Validate(Command("NIT", "ACME SAS")).Errors);

        Assert.Equal("IdentificationNumber", error.PropertyName);
        Assert.Equal(DocumentNumberFormat.NumericTypesMessage, error.ErrorMessage);
    }

    [Fact]
    public void ANitWithDotsAndHyphenIsAccepted() =>
        Assert.True(Rules.Validate(Command("NIT", "900.123.456-7")).IsValid);

    [Fact]
    public void APassportWithLettersIsAccepted() =>
        Assert.True(Rules.Validate(Command("PASAPORTE", "AB123456")).IsValid);
}
