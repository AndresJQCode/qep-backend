using FluentValidation.TestHelper;
using Modules.Catalog.Application;

namespace Modules.Catalog.UnitTests;

/// <summary>
/// Tasas de impuesto: las reglas de tipo, fracción y base las da el dominio con un código; el validador
/// sólo cubre la forma, para que el 422 lleve el mapa <c>errors</c> por campo.
/// </summary>
public sealed class CreateTaxRateValidatorTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private readonly CreateTaxRateValidator _validator = new();

    private static CreateTaxRateCommand Command(
        string name = "IVA general",
        decimal percentage = 19m,
        string type = "IVA",
        string country = "CO") =>
        new(TenantId, name, percentage, type, country, CountryExplicit: false, MinimumBase: null);

    [Fact]
    public void AcceptsAValidRate() =>
        _validator.TestValidate(Command()).ShouldNotHaveAnyValidationErrors();

    [Theory]
    [InlineData(-0.001)]
    [InlineData(100.001)]
    [InlineData(101)]
    public void RejectsAPercentageOutOfRange(double percentage) =>
        _validator.TestValidate(Command(percentage: (decimal)percentage))
            .ShouldHaveValidationErrorFor(command => command.Percentage);

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(0.966)]
    public void AcceptsThePercentageEdges(double percentage) =>
        _validator.TestValidate(Command(percentage: (decimal)percentage))
            .ShouldNotHaveValidationErrorFor(command => command.Percentage);

    [Fact]
    public void RejectsATypeLongerThan32() =>
        _validator.TestValidate(Command(type: new string('a', 33)))
            .ShouldHaveValidationErrorFor(command => command.Type);

    [Theory]
    [InlineData("C")]
    [InlineData("COL")]
    [InlineData("")]
    public void RejectsACountryThatIsNotTwoLetters(string country) =>
        _validator.TestValidate(Command(country: country))
            .ShouldHaveValidationErrorFor(command => command.Country);

    [Fact]
    public void RejectsAnEmptyName() =>
        _validator.TestValidate(Command(name: " "))
            .ShouldHaveValidationErrorFor(command => command.Name);
}
