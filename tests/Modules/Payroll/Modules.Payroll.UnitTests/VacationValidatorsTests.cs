using FluentValidation;
using Modules.Payroll.Application;

namespace Modules.Payroll.UnitTests;

/// <summary>La forma del cuerpo de vacaciones: las claves de <c>errors</c> son las del contrato HTTP.</summary>
public sealed class VacationValidatorsTests
{
    private static readonly DateOnly Start = new(2026, 12, 14);
    private static readonly DateOnly End = new(2027, 1, 5);

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

    private static string[] FailedRequest(VacationRequest request) => Failed(new VacationRequestValidator(), request);

    private static VacationRequest Qep(
        string? origin = "Qep",
        DateOnly? start = null,
        DateOnly? end = null,
        decimal? previousIbc = null,
        int? businessDays = null,
        IReadOnlyList<ExternalVacationMonthRequest>? months = null,
        bool noStart = false,
        bool noEnd = false) =>
        new(origin, noStart ? null : start ?? Start, noEnd ? null : end ?? End, previousIbc, businessDays, months);

    private static VacationRequest External(
        decimal? previousIbc = 2_800_000m, int? businessDays = 9, IReadOnlyList<ExternalVacationMonthRequest>? months = null) =>
        new("External", Start, End, previousIbc, businessDays, months);

    [Fact]
    public void AValidQepRequestPasses() => Assert.Empty(FailedRequest(Qep()));

    [Fact]
    public void AVacationSpansAtMost366Days()
    {
        var start = new DateOnly(2026, 12, 14);
        Assert.Empty(FailedRequest(Qep(start: start, end: start.AddDays(365))));
        Assert.Equal(["endDate"], FailedRequest(Qep(start: start, end: start.AddDays(366))));
        Assert.Equal(["endDate"], FailedRequest(Qep(start: new DateOnly(2026, 1, 1), end: DateOnly.MaxValue)));
    }

    [Fact]
    public void AValidExternalRequestPasses() =>
        Assert.Empty(FailedRequest(External(months:
        [
            new ExternalVacationMonthRequest("2026-12", 900_000m, 36_000m, 36_000m, 0m),
            new ExternalVacationMonthRequest("2027-01", null, null, null, null),
        ])));

    [Fact]
    public void TheOriginIsOneOfTheTwoNames()
    {
        Assert.Equal(["origin"], FailedRequest(Qep(origin: null)));
        Assert.Equal(["origin"], FailedRequest(Qep(origin: "qep")));
        Assert.Equal(["origin"], FailedRequest(Qep(origin: "1")));
    }

    [Fact]
    public void BothDatesAreRequired()
    {
        Assert.Equal(["startDate"], FailedRequest(Qep(noStart: true)));
        Assert.Equal(["endDate"], FailedRequest(Qep(noEnd: true)));
    }

    [Fact]
    public void TheEndCannotPrecedeTheStart() =>
        Assert.Equal(["endDate"], FailedRequest(Qep(start: End, end: Start)));

    [Fact]
    public void AOneDayVacationPasses() => Assert.Empty(FailedRequest(Qep(end: Start)));

    [Fact]
    public void ThePreviousIbcIsPositiveWhenItComes()
    {
        Assert.Equal(["previousMonthIbc"], FailedRequest(Qep(previousIbc: 0m)));
        Assert.Equal(["previousMonthIbc"], FailedRequest(Qep(previousIbc: -1m)));
        Assert.Empty(FailedRequest(Qep(previousIbc: 2_500_000m)));
    }

    [Fact]
    public void ExternalNeedsThePreviousIbcAndTheBusinessDays()
    {
        Assert.Equal(["previousMonthIbc"], FailedRequest(External(previousIbc: null)));
        Assert.Equal(["businessDays"], FailedRequest(External(businessDays: null)));
        Assert.Equal(["businessDays"], FailedRequest(External(businessDays: 0)));
    }

    [Fact]
    public void QepRejectsBusinessDays()
    {
        Assert.Equal(["businessDays"], FailedRequest(Qep(businessDays: 15)));
        Assert.Equal(
            ["externalMonths"],
            FailedRequest(Qep(months: [new ExternalVacationMonthRequest("2026-12", null, null, null, null)])));
        Assert.Empty(FailedRequest(Qep(months: [])));
    }

    [Fact]
    public void AMonthHasTheYearMonthShapeAndFallsInsideTheRange()
    {
        Assert.Equal(
            ["externalMonths[0].month"],
            FailedRequest(External(months: [new ExternalVacationMonthRequest("2026-13", null, null, null, null)])));
        Assert.Equal(
            ["externalMonths[0].month"],
            FailedRequest(External(months: [new ExternalVacationMonthRequest(null, null, null, null, null)])));
        Assert.Equal(
            ["externalMonths[0].month"],
            FailedRequest(External(months: [new ExternalVacationMonthRequest("2026-11", null, null, null, null)])));
        Assert.Equal(
            ["externalMonths[0].month"],
            FailedRequest(External(months: [new ExternalVacationMonthRequest("2027-02", null, null, null, null)])));
    }

    [Fact]
    public void AMonthIsNotRepeated() =>
        Assert.Equal(
            ["externalMonths[1].month"],
            FailedRequest(External(months:
            [
                new ExternalVacationMonthRequest("2026-12", null, null, null, null),
                new ExternalVacationMonthRequest("2026-12", null, null, null, null),
            ])));

    [Fact]
    public void TheAmountsOfAMonthAreNotNegative() =>
        Assert.Equal(
            ["externalMonths[0].vacationPay", "externalMonths[1].healthAdvance", "externalMonths[1].pensionAdvance", "externalMonths[1].solidarityFundAdvance"],
            FailedRequest(External(months:
            [
                new ExternalVacationMonthRequest("2026-12", -1m, 0m, 0m, 0m),
                new ExternalVacationMonthRequest("2027-01", 0m, -1m, -2m, -3m),
            ])));

    [Fact]
    public void ThePreviewNeedsTheOriginAndBothDates()
    {
        var validator = new VacationPreviewRequestValidator();

        Assert.Empty(Failed(validator, new VacationPreviewRequest("External", Start, End)));
        Assert.Equal(["endDate", "origin", "startDate"], Failed(validator, new VacationPreviewRequest(null, null, null)));
        Assert.Equal(["endDate"], Failed(validator, new VacationPreviewRequest("Qep", End, Start)));
    }
}
