using Modules.Wms.Application;

namespace Modules.Wms.UnitTests;

public sealed class CreateLocationValidatorTests
{
    private static readonly CreateLocationCommand Valid = new(
        Guid.NewGuid(), Guid.NewGuid(), null, "A", "Zone", "Storage", true, false, null, null, null, 0, ["FRIO"]);

    private static FluentValidation.Results.ValidationResult Validate(CreateLocationCommand command) =>
        new CreateLocationValidator().Validate(command);

    [Fact]
    public void AValidCommandPasses() => Assert.True(Validate(Valid).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void AnEmptyCodeIsInvalid(string code) =>
        Assert.Contains(Validate(Valid with { Code = code }).Errors, error => error.PropertyName == "Code");

    [Fact]
    public void ACodeLongerThan32IsInvalid() =>
        Assert.Contains(Validate(Valid with { Code = new string('A', 33) }).Errors, error => error.PropertyName == "Code");

    [Theory]
    [InlineData("zone")]
    [InlineData("1")]
    [InlineData("")]
    [InlineData("Nope")]
    public void AKindThatIsNotAnExactNameIsInvalid(string kind) =>
        Assert.Contains(Validate(Valid with { Kind = kind }).Errors, error => error.PropertyName == "Kind");

    [Theory]
    [InlineData("storage")]
    [InlineData("2")]
    [InlineData("Nope")]
    public void ARoleThatIsNotAnExactNameIsInvalid(string role) =>
        Assert.Contains(Validate(Valid with { Role = role }).Errors, error => error.PropertyName == "Role");

    [Fact]
    public void ANegativePutawayPriorityIsInvalid() =>
        Assert.Contains(Validate(Valid with { PutawayPriority = -1 }).Errors, error => error.PropertyName == "PutawayPriority");

    [Fact]
    public void AStorageClassLongerThan32IsReportedByPosition()
    {
        var result = Validate(Valid with { StorageClasses = ["OK", new string('X', 33)] });

        Assert.Contains(result.Errors, error => error.PropertyName == "StorageClasses[1]");
    }

    [Fact]
    public void NoStorageClassesIsValid() => Assert.True(Validate(Valid with { StorageClasses = null }).IsValid);

    [Fact]
    public void TheVirtualKindPassesTheValidatorBecauseTheDomainOwnsThatRule() =>
        Assert.True(Validate(Valid with { Kind = "Virtual" }).IsValid);

    [Theory]
    [InlineData("Active")]
    [InlineData("Blocked")]
    [InlineData("Counting")]
    public void TheUpdateValidatorAcceptsTheStatusNames(string status)
    {
        var command = new UpdateLocationCommand(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "A", "Zone", "Storage", true, false, null, null, null, 0, null, status, 1);

        Assert.True(new UpdateLocationValidator().Validate(command).IsValid);
    }

    [Fact]
    public void TheUpdateValidatorRejectsAnUnknownStatus()
    {
        var command = new UpdateLocationCommand(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "A", "Zone", "Storage", true, false, null, null, null, 0, null, "active", 1);

        Assert.Contains(new UpdateLocationValidator().Validate(command).Errors, error => error.PropertyName == "Status");
    }

    [Theory]
    [InlineData("MaxWeightKg", 1_000_000_000)]
    [InlineData("MaxVolumeM3", 1_000_000)]
    public void ACapacityBeyondTheColumnPrecisionIsInvalidInsteadOfOverflowingTheDatabase(string field, double amount)
    {
        var value = (decimal)amount;
        var create = field == "MaxWeightKg" ? Valid with { MaxWeightKg = value } : Valid with { MaxVolumeM3 = value };
        var update = new UpdateLocationCommand(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "A", "Zone", "Storage", true, false,
            field == "MaxWeightKg" ? value : null, field == "MaxVolumeM3" ? value : null, null, 0, null, "Active", 1);

        Assert.Contains(Validate(create).Errors, error => error.PropertyName == field);
        Assert.Contains(new UpdateLocationValidator().Validate(update).Errors, error => error.PropertyName == field);
    }

    [Fact]
    public void TheLargestCapacitiesThatFitTheColumnsPass()
    {
        Assert.True(Validate(Valid with { MaxWeightKg = 999_999_999.999m, MaxVolumeM3 = 999_999.999999m }).IsValid);
    }

    [Fact]
    public void ANegativeCapacityStaysForTheDomainToRejectWithItsOwnCode() =>
        Assert.True(Validate(Valid with { MaxWeightKg = -1m }).IsValid);
}
