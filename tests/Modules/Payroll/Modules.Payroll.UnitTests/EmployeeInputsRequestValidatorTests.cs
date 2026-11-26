using FluentValidation;
using Modules.Payroll.Application;

namespace Modules.Payroll.UnitTests;

/// <summary>La forma del cuerpo de capturas y del de recurrentes. Las claves de <c>errors</c> son las del contrato HTTP.</summary>
public sealed class EmployeeInputsRequestValidatorTests
{
    private static readonly Guid ExtraDay = Guid.Parse("00000000-0000-0000-0001-000000000001");
    private static readonly Guid ExtraNight = Guid.Parse("00000000-0000-0000-0001-000000000002");
    private static readonly Guid Commissions = Guid.Parse("00000000-0000-0000-0002-000000000001");
    private static readonly Guid OtherDeduction = Guid.Parse("00000000-0000-0000-0003-000000000001");

    private static string[] Failed<T>(PayrollValidator<T> validator, T request)
    {
        try
        {
            validator.ValidateAndThrow(request);
            return [];
        }
        catch (ValidationException exception)
        {
            return exception.Errors.Select(failure => failure.PropertyName).Distinct().Order().ToArray();
        }
    }

    private static string[] FailedInputs(EmployeeInputsRequest request) => Failed(new EmployeeInputsRequestValidator(), request);

    private static EmployeeInputsRequest Inputs(
        OvertimeInputRequest[]? overtime = null, IncomeInputRequest[]? incomes = null, DeductionInputRequest[]? deductions = null) =>
        new(overtime ?? [], incomes ?? [], deductions ?? []);

    [Fact]
    public void AnEmptyRequestPasses() => Assert.Empty(FailedInputs(Inputs()));

    [Fact]
    public void AFullValidRequestPasses() =>
        Assert.Empty(FailedInputs(Inputs(
            [new OvertimeInputRequest(ExtraDay, 4.5m), new OvertimeInputRequest(ExtraNight, 360m)],
            [new IncomeInputRequest(Commissions, 500_000m, true)],
            [new DeductionInputRequest(OtherDeduction, 20_000.5m)])));

    [Fact]
    public void HoursArePositiveWithTwoDecimals()
    {
        Assert.Equal(["overtime[0].hours"], FailedInputs(Inputs([new OvertimeInputRequest(ExtraDay, 0m)])));
        Assert.Equal(["overtime[0].hours"], FailedInputs(Inputs([new OvertimeInputRequest(ExtraDay, -1m)])));
        Assert.Equal(["overtime[0].hours"], FailedInputs(Inputs([new OvertimeInputRequest(ExtraDay, 1.234m)])));
        Assert.Equal(["overtime[0].hours"], FailedInputs(Inputs([new OvertimeInputRequest(ExtraDay, null)])));
    }

    [Fact]
    public void HoursAtMostThreeHundredSixty()
    {
        Assert.Equal(["overtime[0].hours"], FailedInputs(Inputs([new OvertimeInputRequest(ExtraDay, 360.01m)])));
        Assert.Empty(FailedInputs(Inputs([new OvertimeInputRequest(ExtraDay, 360m)])));
    }

    [Fact]
    public void AmountsArePositiveWithTwoDecimals()
    {
        Assert.Equal(["incomes[0].amount"], FailedInputs(Inputs(incomes: [new IncomeInputRequest(Commissions, 0m, true)])));
        Assert.Equal(["incomes[0].amount"], FailedInputs(Inputs(incomes: [new IncomeInputRequest(Commissions, 10.001m, true)])));
        Assert.Equal(
            ["incomes[0].amount"],
            FailedInputs(Inputs(incomes: [new IncomeInputRequest(Commissions, 10_000_000_000_000_000m, true)])));
        Assert.Equal(["deductions[1].amount"], FailedInputs(Inputs(
            deductions: [new DeductionInputRequest(OtherDeduction, 5m), new DeductionInputRequest(OtherDeduction, -5m)])));
    }

    [Fact]
    public void AnOvertimeConceptOnlyOnce() =>
        Assert.Equal(
            ["overtime[1].conceptId"],
            FailedInputs(Inputs([new OvertimeInputRequest(ExtraDay, 2m), new OvertimeInputRequest(ExtraDay, 3m)])));

    [Fact]
    public void IncomesNeedTheirClassification() =>
        Assert.Equal(
            ["incomes[0].salaryConstitutive"],
            FailedInputs(Inputs(incomes: [new IncomeInputRequest(Commissions, 1_000m, null)])));

    [Fact]
    public void EveryLineNeedsItsConcept() =>
        Assert.Equal(
            ["deductions[0].conceptId"],
            FailedInputs(Inputs(deductions: [new DeductionInputRequest(Guid.Empty, 1_000m)])));

    [Fact]
    public void ListsHaveTheirLimits()
    {
        Assert.Equal(
            ["overtime"],
            FailedInputs(Inputs(Enumerable.Range(0, 21).Select(_ => new OvertimeInputRequest(Guid.NewGuid(), 1m)).ToArray())));
        Assert.Equal(
            ["incomes"],
            FailedInputs(Inputs(incomes: Enumerable.Range(0, 51).Select(_ => new IncomeInputRequest(Commissions, 1m, true)).ToArray())));
        Assert.Equal(
            ["deductions"],
            FailedInputs(Inputs(deductions: Enumerable.Range(0, 51).Select(_ => new DeductionInputRequest(OtherDeduction, 1m)).ToArray())));
        Assert.Empty(FailedInputs(Inputs(Enumerable.Range(0, 20).Select(_ => new OvertimeInputRequest(Guid.NewGuid(), 1m)).ToArray())));
    }

    [Fact]
    public void ABodyWithoutTheListsFails() =>
        Assert.Equal(["deductions", "incomes", "overtime"], FailedInputs(new EmployeeInputsRequest(null, null, null)));

    private static string[] FailedRecurring(RecurringConceptRequest request) =>
        Failed(new RecurringConceptRequestValidator(), request);

    [Fact]
    public void AValidRecurringPasses()
    {
        Assert.Empty(FailedRecurring(new RecurringConceptRequest(Commissions, "Fixed", 100_000m, false, 1_200_000m)));
        Assert.Empty(FailedRecurring(new RecurringConceptRequest(OtherDeduction, "Proportional", 100m, null, null)));
    }

    [Fact]
    public void ARecurringNeedsAKnownModeAnAmountAndACapAtLeastTheAmount()
    {
        Assert.Equal(["mode"], FailedRecurring(new RecurringConceptRequest(Commissions, "Fijo", 100m, false, null)));
        Assert.Equal(["amount"], FailedRecurring(new RecurringConceptRequest(Commissions, "Fixed", 0m, false, null)));
        Assert.Equal(["cap"], FailedRecurring(new RecurringConceptRequest(Commissions, "Fixed", 100m, false, 99m)));
        Assert.Equal(["conceptId"], FailedRecurring(new RecurringConceptRequest(null, "Fixed", 100m, false, null)));
    }
}
