using System.Globalization;
using Modules.Loans.Application;
using static Modules.Loans.UnitTests.LoanTestData;

namespace Modules.Loans.UnitTests;

public sealed class CapitalPaymentValidatorTests
{
    private static readonly AddCapitalPaymentCommand Valid = new(Guid.NewGuid(), Guid.NewGuid(), 1_000m, Date("2026-10-15"), null);

    private static bool IsValid(AddCapitalPaymentCommand command) => new AddCapitalPaymentValidator().Validate(command).IsValid;

    [Fact]
    public void AValidCommandPassesInBothValidators()
    {
        Assert.True(IsValid(Valid));
        Assert.True(new PreviewCapitalPaymentValidator().Validate(
            new PreviewCapitalPaymentCommand(Valid.TenantId, Valid.LoanId, Valid.Amount, Valid.PaidOn, null)).IsValid);
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("10.001", false)]
    [InlineData("10.01", true)]
    [InlineData("10.10", true)]
    // numeric(18,2): 16 enteros y 2 decimales caben, 17 enteros no.
    [InlineData("9999999999999999.99", true)]
    [InlineData("99999999999999999.99", false)]
    public void TheAmountIsPositiveAndFitsNumeric18Dot2(string amount, bool valid)
    {
        var parsed = decimal.Parse(amount, CultureInfo.InvariantCulture);

        Assert.Equal(valid, IsValid(Valid with { Amount = parsed }));
        Assert.Equal(
            valid,
            new PreviewCapitalPaymentValidator().Validate(
                new PreviewCapitalPaymentCommand(Valid.TenantId, Valid.LoanId, parsed, Valid.PaidOn, null)).IsValid);
    }

    [Fact]
    public void AMissingDateIsReportedByName() =>
        Assert.Contains(
            new AddCapitalPaymentValidator().Validate(Valid with { PaidOn = default }).Errors,
            error => error.PropertyName == "PaidOn");

    [Fact]
    public void TheNoteIsMeasuredTrimmed()
    {
        Assert.True(IsValid(Valid with { Note = new string('x', 1000) + "   " }));
        Assert.False(IsValid(Valid with { Note = new string('x', 1001) }));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("   ", false)]
    [InlineData("error de digitación", true)]
    public void TheVoidReasonIsRequired(string? reason, bool valid)
    {
        var command = VoidCapitalPaymentCommand.From(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new VoidCapitalPaymentRequest(reason));

        var result = new VoidCapitalPaymentValidator().Validate(command);

        Assert.Equal(valid, result.IsValid);
        Assert.Equal(!valid, result.Errors.Any(error => error.PropertyName == "Reason"));
    }

    [Fact]
    public void TheVoidReasonHasAnUpperBound()
    {
        var at = new VoidCapitalPaymentCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('x', 500));
        var over = at with { Reason = new string('x', 501) };

        Assert.True(new VoidCapitalPaymentValidator().Validate(at).IsValid);
        Assert.False(new VoidCapitalPaymentValidator().Validate(over).IsValid);
    }
}
