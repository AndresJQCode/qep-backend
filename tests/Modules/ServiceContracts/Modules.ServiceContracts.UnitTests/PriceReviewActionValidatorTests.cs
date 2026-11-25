using System.Globalization;
using Modules.ServiceContracts.Application;

namespace Modules.ServiceContracts.UnitTests;

/// <summary>Los validadores de las acciones por contrato (spec reajuste §8): nombran el campo en el <c>validation.failed</c>.</summary>
public sealed class PriceReviewActionValidatorTests
{
    // El 10 de enero de 2027 en Bogotá: el techo de las fechas es el 10 de enero de 2037.
    private static readonly FixedTenantClock Clock = new(new DateTimeOffset(2027, 1, 10, 15, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static decimal? Number(string? text) => text is null ? null : decimal.Parse(text, CultureInfo.InvariantCulture);

    private static DateOnly? Date(string? text) =>
        text is null ? null : DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<bool> FailsAsync(SchedulePriceChangeRequest request, string property, long version = 1) =>
        (await new SchedulePriceChangeValidator(Clock).ValidateAsync(
            SchedulePriceChangeCommand.From(Guid.NewGuid(), Guid.NewGuid(), request, version), Ct))
        .Errors.Any(error => error.PropertyName == property);

    private static async Task<bool> FailsAsync(PriceReviewInput input, string property, long version = 1) =>
        (await new UpdatePriceReviewValidator().ValidateAsync(
            UpdatePriceReviewCommand.From(Guid.NewGuid(), Guid.NewGuid(), input, version), Ct))
        .Errors.Any(error => error.PropertyName == property);

    [Theory]
    [InlineData(null, true)]
    [InlineData("0", true)]
    [InlineData("-1", true)]
    [InlineData("10.123", true)]
    [InlineData("10.12", false)]
    public async Task TheNewPriceIsAPositivePriceWithTwoDecimals(string? price, bool rejected) =>
        Assert.Equal(rejected, await FailsAsync(new SchedulePriceChangeRequest(Number(price), null, "Más usuarios"), "NewPrice"));

    [Theory]
    [InlineData(500, false)]
    [InlineData(501, true)]
    public async Task TheScheduleReasonFitsTheColumn(int length, bool rejected) =>
        Assert.Equal(rejected, await FailsAsync(new SchedulePriceChangeRequest(100m, null, new string('x', length)), "Reason"));

    // Vacío lo responde el dominio con su código (billing.service.price_reason_required), no el validador.
    [Fact]
    public async Task AnEmptyScheduleReasonIsLeftToTheDomain() =>
        Assert.False(await FailsAsync(new SchedulePriceChangeRequest(100m, null, null), "Reason"));

    [Theory]
    [InlineData(null, false)]
    [InlineData("2027-03-10", false)]
    [InlineData("2037-01-10", false)]
    [InlineData("2037-01-11", true)]
    [InlineData("9999-12-31", true)]
    public async Task TheEffectiveDateIsAtMostTenYearsAhead(string? date, bool rejected) =>
        Assert.Equal(rejected, await FailsAsync(new SchedulePriceChangeRequest(100m, Date(date), "Más usuarios"), "EffectiveFrom"));

    [Theory]
    [InlineData("Manual", false)]
    [InlineData("Ipc", false)]
    [InlineData("Annual", true)]
    [InlineData("ipc", true)]
    [InlineData(null, true)]
    public async Task TheReviewModeIsOneOfTheTwoWireNames(string? mode, bool rejected) =>
        Assert.Equal(rejected, await FailsAsync(new PriceReviewInput(mode, 0m, new DateOnly(2027, 6, 10)), "AdjustmentMode"));

    [Theory]
    [InlineData("0", false)]
    [InlineData("50", false)]
    [InlineData("50.01", true)]
    [InlineData("-0.01", true)]
    [InlineData("1.005", true)]
    [InlineData(null, true)]
    public async Task TheReviewPointsAreRequiredAndBounded(string? points, bool rejected) =>
        Assert.Equal(
            rejected, await FailsAsync(new PriceReviewInput("Ipc", Number(points), new DateOnly(2027, 6, 10)), "IpcExtraPoints"));

    // El techo de diez años de esta fecha lo pone el handler, que conoce la guardada: sólo lo exige a una fecha que cambia.
    [Theory]
    [InlineData(null, true)]
    [InlineData("2027-06-10", false)]
    [InlineData("2037-01-11", false)]
    public async Task TheReviewDateIsRequired(string? date, bool rejected) =>
        Assert.Equal(rejected, await FailsAsync(new PriceReviewInput("Ipc", 0m, Date(date)), "NextReviewDate"));

    [Fact]
    public async Task TheVersionIsRequiredByEveryAction()
    {
        Assert.True(await FailsAsync(new PriceReviewInput("Ipc", 0m, new DateOnly(2027, 6, 10)), "ExpectedVersion", version: 0));
        Assert.True(await FailsAsync(new SchedulePriceChangeRequest(100m, null, "x"), "ExpectedVersion", version: 0));
        Assert.False((await new CancelPriceScheduleValidator().ValidateAsync(
            new CancelPriceScheduleCommand(Guid.NewGuid(), Guid.NewGuid(), 0), Ct)).IsValid);
        Assert.False((await new KeepPriceValidator().ValidateAsync(
            new KeepPriceCommand(Guid.NewGuid(), Guid.NewGuid(), 0), Ct)).IsValid);
    }
}
