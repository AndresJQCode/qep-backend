using Modules.Companies.Application;

namespace Modules.Companies.UnitTests;

/// <summary>Spec D6: the account currency decides the currency of every quotation billed to it,
/// so it must be a catalogue code — not just "three letters" as before.</summary>
public sealed class CompanyWriteRulesTests
{
    private static CreateCompanyCommand Command(string currency) =>
        new(
            Guid.NewGuid(),
            "QEP Comercial S.A.S.",
            [new CompanyBankAccountPayload("Bancolombia", "12345678", currency)],
            "901.123.456-2",
            Guid.NewGuid(),
            null,
            null,
            null);

    [Theory]
    [InlineData("EUR")]
    [InlineData("cop")]
    public void ACatalogueCurrencyIsValid(string currency)
    {
        var result = new CreateCompanyValidator().Validate(Command(currency));

        Assert.DoesNotContain(result.Errors, error => error.PropertyName == "BankAccounts[0].Currency");
    }

    [Theory]
    [InlineData("MXN")]
    [InlineData("ABC")]
    public void ACurrencyOutsideTheCatalogueFailsOnItsRow(string currency)
    {
        var result = new CreateCompanyValidator().Validate(Command(currency));

        Assert.Contains(result.Errors, error => error.PropertyName == "BankAccounts[0].Currency");
    }
}
