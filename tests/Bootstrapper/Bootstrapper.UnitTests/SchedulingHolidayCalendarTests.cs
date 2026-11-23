using Modules.Messaging.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-11-21 (Agenda), D-10: las franjas que cierran en festivos descuentan los festivos nacionales de Colombia sólo en un tenant de
/// Colombia (<see cref="ColombianHolidays"/>, que vive en Mensajería y por eso este adaptador está en Bootstrapper); en cualquier otro
/// país no se cierra nada. Mismo criterio que <c>SupportBusinessCalendar</c>.
/// </summary>
public sealed class SchedulingHolidayCalendarTests
{
    private static readonly DateOnly HolidayMonday = new(2026, 11, 16);

    [Fact]
    public void AColombianTenantGetsTheNationalHolidaysOfTheRange()
    {
        var holidays = SchedulingHolidays.Between("CO", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30));

        Assert.Contains(HolidayMonday, holidays);
        Assert.Contains(new DateOnly(2026, 11, 2), holidays);
        Assert.DoesNotContain(new DateOnly(2026, 11, 17), holidays);
    }

    [Theory]
    [InlineData("ES")]
    [InlineData("")]
    [InlineData("  ")]
    public void AnyOtherCountryHasNoHolidays(string country) =>
        Assert.Empty(SchedulingHolidays.Between(country, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)));

    [Fact]
    public void TheCountryIsComparedWithoutCaseOrSpaces() =>
        Assert.Contains(HolidayMonday, SchedulingHolidays.Between(" co ", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30)));

    [Fact]
    public void ARangeThatCrossesNewYearTakesBothYears()
    {
        var holidays = SchedulingHolidays.Between("CO", new DateOnly(2026, 12, 20), new DateOnly(2027, 1, 15));

        Assert.Contains(new DateOnly(2026, 12, 25), holidays);
        Assert.Contains(new DateOnly(2027, 1, 1), holidays);
        Assert.DoesNotContain(new DateOnly(2026, 12, 8), holidays);
    }

    [Fact]
    public void ABackwardsRangeIsEmpty() =>
        Assert.Empty(SchedulingHolidays.Between("CO", new DateOnly(2026, 12, 31), new DateOnly(2026, 1, 1)));

    [Fact]
    public async Task TheAdapterReadsTheCountryOfTheTenant()
    {
        var tenant = Guid.CreateVersion7();
        var colombia = new SchedulingHolidayCalendar(new Directory("CO"));
        var spain = new SchedulingHolidayCalendar(new Directory("ES"));
        var from = new DateOnly(2026, 11, 1);
        var to = new DateOnly(2026, 11, 30);

        Assert.Contains(HolidayMonday, await colombia.HolidaysAsync(tenant, from, to, CancellationToken.None));
        Assert.Empty(await spain.HolidaysAsync(tenant, from, to, CancellationToken.None));
    }

    [Fact]
    public async Task ATenantWithoutRegionalSettingsFailsInsteadOfGuessingTheCountry()
    {
        var calendar = new SchedulingHolidayCalendar(new Directory(null));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            calendar.HolidaysAsync(Guid.CreateVersion7(), new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), CancellationToken.None));
    }

    private sealed class Directory(string? country) : ITenantDirectory
    {
        public Task<TenantRegionalSettings?> GetRegionalSettingsAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<TenantRegionalSettings?>(country is null
                ? null
                : new TenantRegionalSettings("America/Bogota", "dd/MM/yyyy", "COP", "es-CO", country));

        public Task<string?> GetSlugAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> GetDisplayNameAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> GetTimeZoneAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> GetDefaultCurrencyAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Guid?> GetLogoFileIdAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TenantStatus?> GetStatusAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
