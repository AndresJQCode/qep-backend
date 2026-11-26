using Modules.Wms.Application;

namespace Modules.Wms.UnitTests;

public sealed class CreateWarehouseValidatorTests
{
    private static readonly CreateWarehouseCommand Valid = new(Guid.NewGuid(), "BOG", "Bogotá", null);

    private static FluentValidation.Results.ValidationResult Validate(CreateWarehouseCommand command) =>
        new CreateWarehouseValidator().Validate(command);

    [Fact]
    public void AValidCommandPasses() => Assert.True(Validate(Valid).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyCodeIsInvalid(string code)
    {
        var result = Validate(Valid with { Code = code });

        Assert.Contains(result.Errors, error => error.PropertyName == "Code");
    }

    [Fact]
    public void ACodeLongerThan32IsInvalid()
    {
        var result = Validate(Valid with { Code = new string('A', 33) });

        Assert.Contains(result.Errors, error => error.PropertyName == "Code");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void AnEmptyNameIsInvalid(string name)
    {
        var result = Validate(Valid with { Name = name });

        Assert.Contains(result.Errors, error => error.PropertyName == "Name");
    }

    [Fact]
    public void ANameLongerThan120IsInvalidInsteadOfReachingTheDomainGuard()
    {
        var result = Validate(Valid with { Name = new string('x', 121) });

        Assert.Contains(result.Errors, error => error.PropertyName == "Name");
    }
}
