using BuildingBlocks.Application;
using Modules.Geography.Application;
using Modules.Siigo.Application.Invoicing;

namespace Bootstrapper.UnitTests;

/// <summary>Spec 2026-11-26: el adaptador de Siigo sobre Geography. Anotar manda el pedido y el texto al reportero (con la hora del reloj)
/// y resolver traduce el resultado del resolvedor; ninguno decide nada por su cuenta.</summary>
public sealed class SiigoForeignCityInboxTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid OrderId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 15, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheInboxReportsTheTenantOrderCountryAndTextAtTheClockTime()
    {
        var reporter = new RecordingReporter();
        var inbox = new SiigoForeignCityInbox(reporter, new FixedClock());

        await inbox.ReportAsync(TenantId, OrderId, new SiigoUnlinkedCity("US", "Miamy", Reportable: true), TestContext.Current.CancellationToken);

        var report = Assert.Single(reporter.Reports);
        Assert.Equal((TenantId, OrderId, "US", "Miamy", Now), (report.TenantId, report.OrderId, report.CountryCode, report.CityText, report.SeenAt));
    }

    [Theory]
    [InlineData(ForeignCityResolutionKind.CountryNotInCatalog, false, false)]
    [InlineData(ForeignCityResolutionKind.Unresolved, true, false)]
    [InlineData(ForeignCityResolutionKind.Matched, true, true)]
    public async Task TheLookupTranslatesTheResolution(ForeignCityResolutionKind kind, bool countryInCatalog, bool hasCodes)
    {
        var codes = kind == ForeignCityResolutionKind.Matched ? new BillingCityCodes("Us", "09", "1") : null;
        var lookup = new SiigoGeographyLookup(null!, null!, new FixedResolver(new ForeignCityResolution(kind, codes)));

        var result = await lookup.FindForeignCodesAsync("US", "Miami", TestContext.Current.CancellationToken);

        Assert.Equal(countryInCatalog, result.CountryInCatalog);
        Assert.Equal(hasCodes ? new SiigoForeignCityCodes("Us", "09", "1") : null, result.Codes);
    }

    private sealed class RecordingReporter : IForeignCityReporter
    {
        public List<ForeignCityReport> Reports { get; } = [];

        public Task<ForeignCityReportOutcome> ReportAsync(ForeignCityReport report, CancellationToken cancellationToken)
        {
            Reports.Add(report);
            return Task.FromResult(ForeignCityReportOutcome.Recorded);
        }
    }

    private sealed class FixedResolver(ForeignCityResolution resolution) : IForeignBillingCityResolver
    {
        public Task<ForeignCityResolution> ResolveAsync(string countryCode, string cityText, CancellationToken cancellationToken) =>
            Task.FromResult(resolution);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
