using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

public sealed class UpdateTenantSettingsHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly Tenant _tenant = Tenant.Create(
        TenantId.New(), "qcode-demo", "QCode Demo", "es-CO", "America/Bogota", "yyyy-MM-dd",
        MembershipId.New(), Now);

    [Fact]
    public async Task HandlePassesCurrencyAndNumberFormatToTheAggregate()
    {
        var result = await Handler().HandleAsync(
            Command(defaultCurrency: "usd", numberFormat: "1,234.56"),
            TestContext.Current.CancellationToken);

        Assert.Equal("USD", result.DefaultCurrency);
        Assert.Equal("1,234.56", result.NumberFormat);
        Assert.Equal(2, result.Version);
    }

    [Fact]
    public async Task HandleRejectsAnEmptyCurrencyThroughTheValidator()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            Handler().HandleAsync(Command(defaultCurrency: ""), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HandlePassesTheCountryToTheAggregate()
    {
        var result = await Handler().HandleAsync(
            Command(country: "mx"),
            TestContext.Current.CancellationToken);

        Assert.Equal("MX", result.Country);
        Assert.Equal(2, result.Version);
    }

    // Ventana de despliegue: un frontend anterior al país no lo manda. Sin él se conserva el
    // guardado, y como nada más cambió, la versión no sube.
    [Fact]
    public async Task HandleKeepsTheStoredCountryWhenTheCommandOmitsIt()
    {
        var result = await Handler().HandleAsync(
            Command(country: null),
            TestContext.Current.CancellationToken);

        Assert.Equal("CO", result.Country);
        Assert.Equal(1, result.Version);
    }

    private UpdateTenantSettingsCommand Command(
        string defaultCurrency = "COP", string numberFormat = "1.234,56", string? country = "CO") =>
        new(_tenant.Id, "QCode Demo", "es-CO", "America/Bogota", "yyyy-MM-dd",
            defaultCurrency, numberFormat, country, 1, "corr-1");

    private UpdateTenantSettingsHandler Handler() =>
        new(
            new InMemoryTenantRepository(_tenant),
            new RecordingTenancyUnitOfWork([]),
            new SettingsUpdateExecutionContext(_tenant.Id),
            new RecordingAuditRecorder(),
            new RecordingOutboxWriter(),
            new FixedClock(Now),
            new UpdateTenantSettingsValidator(),
            new RecordingTenantLogoStorage([]));
}
