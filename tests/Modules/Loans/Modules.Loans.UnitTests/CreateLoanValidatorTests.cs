using FluentValidation.Results;
using Modules.Loans.Application;
using static Modules.Loans.UnitTests.LoanTestData;

namespace Modules.Loans.UnitTests;

public sealed class CreateLoanValidatorTests
{
    private static readonly CreateLoanCommand Valid = new(
        Guid.NewGuid(), Guid.NewGuid(), "Novasteel", 100_000_000m, null, 1.5m, Date("2026-10-01"), "EndOfMonth", null, null);

    private static ValidationResult Validate(CreateLoanCommand command) => new CreateLoanValidator().Validate(command);

    [Fact]
    public void AValidCommandPasses() => Assert.True(Validate(Valid).IsValid);

    [Theory]
    [InlineData("Name")]
    [InlineData("PrincipalAmount")]
    [InlineData("MonthlyRatePercent")]
    [InlineData("DisbursedOn")]
    [InlineData("CutoffRule")]
    [InlineData("Note")]
    [InlineData("CustomerId")]
    public void AnInvalidFieldIsReportedByName(string property)
    {
        var command = property switch
        {
            "Name" => Valid with { Name = " " },
            "PrincipalAmount" => Valid with { PrincipalAmount = 0m },
            "MonthlyRatePercent" => Valid with { MonthlyRatePercent = 0m },
            // Un request sin fecha llega como default (0001-01-01): queda bajo el piso.
            "DisbursedOn" => Valid with { DisbursedOn = default },
            "CutoffRule" => Valid with { CutoffRule = "Weekly" },
            "Note" => Valid with { Note = new string('x', 1001) },
            "CustomerId" => Valid with { CustomerId = Guid.Empty },
            _ => throw new ArgumentOutOfRangeException(nameof(property)),
        };

        var result = Validate(command);

        Assert.Contains(result.Errors, error => error.PropertyName == property);
    }

    [Theory]
    [InlineData("1999-12-31", false)]
    [InlineData("2000-01-01", true)]
    public void TheDisbursementHasAFloorButNoCeilingHere(string date, bool valid) =>
        // El techo lo juzga Loan.Create con la «hoy» del tenant: loans.loan.disbursed_in_future.
        Assert.Equal(valid, Validate(Valid with { DisbursedOn = Date(date) }).IsValid);

    [Theory]
    [InlineData("10.001", false)]
    [InlineData("10.01", true)]
    [InlineData("10.10", true)]
    // numeric(18,2): 16 dígitos enteros y 2 decimales caben, 17 enteros no.
    [InlineData("9999999999999999.99", true)]
    [InlineData("99999999999999999.99", false)]
    public void TheMoneyAmountFitsNumeric18Dot2(string amount, bool valid)
    {
        var result = Validate(Valid with { PrincipalAmount = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture) });

        Assert.Equal(valid, result.IsValid);
        Assert.Equal(!valid, result.Errors.Any(error => error.PropertyName == "PrincipalAmount"));
    }

    [Theory]
    [InlineData("1.5", true)]
    [InlineData("1.2345", true)]
    [InlineData("1.23456", false)]
    [InlineData("100", true)]
    [InlineData("100.0001", false)]
    [InlineData("0", false)]
    public void TheRateFitsNumeric7Dot4AndTheContractRange(string rate, bool valid) =>
        Assert.Equal(
            valid,
            Validate(Valid with { MonthlyRatePercent = decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture) }).IsValid);

    [Fact]
    public void AnUpdateNeedsAVersionAndTheSameRules()
    {
        var update = new UpdateLoanCommand(
            Guid.NewGuid(), Guid.NewGuid(), Valid.CustomerId, "Novasteel", 100_000_000m, null, 1.5m, Date("2026-10-01"), "EndOfMonth", null, null, 0);

        var result = new UpdateLoanValidator().Validate(update);

        Assert.Equal(["ExpectedVersion"], result.Errors.Select(error => error.PropertyName).ToArray());
    }
}
