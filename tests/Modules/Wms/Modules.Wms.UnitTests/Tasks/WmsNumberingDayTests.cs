using Modules.Tenancy.Application;
using Modules.Wms.Application;

namespace Modules.Wms.UnitTests.Tasks;

/// <summary>B5 (owner, 2026-11-27): el día de los números diarios (<c>TSK-</c>, <c>ADJ-</c>, <c>INC-</c>) es el del huso del tenant.</summary>
public sealed class WmsNumberingDayTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private sealed class FakeTenantClock(string timeZoneId) : ITenantClock
    {
        public Guid? AskedFor { get; private set; }

        public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken)
        {
            AskedFor = tenantId;
            // El instante del calendario no se usa: el día se corta para el instante del handler.
            return Task.FromResult(new TenantCalendar(DateTimeOffset.UnixEpoch, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)));
        }
    }

    [Fact]
    public async Task AnInstantAfterSevenInTheEveningInBogotaStillBelongsToThatDay()
    {
        var clock = new FakeTenantClock("America/Bogota");
        var instant = new DateTimeOffset(2026, 11, 28, 3, 0, 0, TimeSpan.Zero);

        var day = await new WmsNumberingDay(clock).OfAsync(Tenant, instant, TestContext.Current.CancellationToken);

        Assert.Equal(new DateOnly(2026, 11, 27), day);
        Assert.Equal(Tenant, clock.AskedFor);
    }

    [Fact]
    public async Task AnInstantAfterFiveInTheMorningInBogotaIsTheSameUtcDay()
    {
        var instant = new DateTimeOffset(2026, 11, 28, 5, 0, 0, TimeSpan.Zero);

        var day = await new WmsNumberingDay(new FakeTenantClock("America/Bogota"))
            .OfAsync(Tenant, instant, TestContext.Current.CancellationToken);

        Assert.Equal(new DateOnly(2026, 11, 28), day);
    }

    [Fact]
    public async Task ATenantInUtcCutsTheDayAtMidnightUtc()
    {
        var instant = new DateTimeOffset(2026, 11, 28, 3, 0, 0, TimeSpan.Zero);

        var day = await new WmsNumberingDay(new FakeTenantClock("UTC"))
            .OfAsync(Tenant, instant, TestContext.Current.CancellationToken);

        Assert.Equal(new DateOnly(2026, 11, 28), day);
    }
}
