using FluentValidation.Results;
using Modules.Wms.Application;

namespace Modules.Wms.UnitTests.Tasks;

/// <summary>El dominio ya frena casi todo, pero con un código o con un <c>ArgumentException</c> (500): el validador da el 422
/// <c>validation.failed</c> con el campo.</summary>
public sealed class CreateMoveTaskValidatorTests
{
    private static readonly MoveTaskLineRequest Line =
        new(Guid.NewGuid(), null, null, Guid.NewGuid(), Guid.NewGuid(), 5);

    private static readonly CreateMoveTaskCommand Valid =
        new(Guid.NewGuid(), Guid.NewGuid(), null, null, null, null, true, null, [Line]);

    private static ValidationResult Validate(CreateMoveTaskCommand command) => new CreateMoveTaskValidator().Validate(command);

    private static void AssertInvalid(CreateMoveTaskCommand command, string propertyName) =>
        Assert.Contains(Validate(command).Errors, error => error.PropertyName == propertyName);

    [Fact]
    public void AValidCommandPasses() => Assert.True(Validate(Valid).IsValid);

    [Fact]
    public void ANoteOfFiveHundredCharactersAfterTrimmingPasses() =>
        Assert.True(Validate(Valid with { Note = $"  {new string('n', 500)}  " }).IsValid);

    [Fact]
    public void ANoteOverFiveHundredCharactersIsInvalid() => AssertInvalid(Valid with { Note = new string('n', 501) }, "Note");

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void AMoveHasBetweenOneAndOneHundredLines(int count) =>
        AssertInvalid(Valid with { Lines = Enumerable.Repeat(Line, count).ToArray() }, "Lines");

    [Fact]
    public void ANullLineIsInvalid() => AssertInvalid(Valid with { Lines = [null!] }, "Lines[0]");

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1000000000000")]
    public void AQuantityOutsideTheColumnRangeIsInvalid(string quantity) =>
        AssertInvalid(
            Valid with
            {
                Lines = [Line with { Quantity = decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture) }],
            },
            "Lines[0].Quantity");

    [Theory]
    [InlineData("Banana")]
    [InlineData("available")]
    [InlineData("")]
    public void AStockStatusOutsideTheEnumIsInvalid(string status) =>
        AssertInvalid(Valid with { Lines = [Line with { StockStatus = status }] }, "Lines[0].StockStatus");

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void APriorityOutsideOneToNineIsInvalid(int priority) => AssertInvalid(Valid with { Priority = priority }, "Priority");

    [Theory]
    [InlineData("Banana", null, "Status")]
    [InlineData(null, "Pick", "Type")]
    public void AListFilterOutsideItsEnumIsInvalid(string? status, string? type, string property)
    {
        var result = new ListTasksValidator().Validate(new ListTasksQuery(Guid.NewGuid(), null, status, type, null, 1, 50));

        Assert.Contains(result.Errors, error => error.PropertyName == property);
    }
}
