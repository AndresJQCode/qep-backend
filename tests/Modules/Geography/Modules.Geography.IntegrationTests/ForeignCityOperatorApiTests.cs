using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Geography.Infrastructure.Persistence;
using static Modules.Geography.IntegrationTests.GeographyApiHarness;

namespace Modules.Geography.IntegrationTests;

/// <summary>
/// La consola de ciudades del exterior del operador de punta a punta (spec 2026-11-26 «Consola del operador»), con el stub de
/// desarrollo. El tenant operador es simulado (sin fila en <c>tenancy.tenants</c>), como en <c>CityOperatorApiTests</c>. Las
/// filas de la bandeja las siembra el <see cref="IForeignCityReporter"/> real, el mismo que llamará la facturación de Siigo.
/// </summary>
public sealed class ForeignCityOperatorApiTests
{
    private static readonly Guid OperatorTenantId = Guid.Parse("01900000-0000-7000-8000-00000000c0de");
    private static readonly Guid OperatorSubjectId = Guid.Parse("01900000-0000-7000-8000-0000000000aa");
    private static readonly Guid ReportTenantId = Guid.Parse("01900000-0000-7000-8000-0000000000bb");
    private static readonly Guid OtherReportTenantId = Guid.Parse("01900000-0000-7000-8000-0000000000cc");
    private static readonly DateTimeOffset Seen = new(2026, 11, 26, 15, 0, 0, TimeSpan.Zero);

    private static readonly string[] ReadAndManage =
        [GeographyOperatorPermissions.CitiesRead, GeographyOperatorPermissions.CitiesManage];

    private static string Base => $"/api/v1/tenants/{OperatorTenantId}/operator/cities/foreign";

    // Sin la política registrada el síntoma sería 500, no 403. Un id que no existe también es 403: el 403 no depende del id.
    [Fact]
    public async Task EveryForeignReadRouteAnswers403WithoutThePermission()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, "tenancy.settings.read");

        string[] routes =
        [
            $"{Base}/countries",
            $"{Base}/catalog?country=US",
            $"{Base}/unresolved",
            $"{Base}/unresolved/{Guid.CreateVersion7()}",
            $"{Base}/synonyms",
        ];
        foreach (var route in routes)
        {
            var response = await client.GetAsync(route, TestContext.Current.CancellationToken);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{route} answered {response.StatusCode}.");
        }
    }

    [Fact]
    public async Task TheCountriesPutThoseWithPendingFirstAndThenByName()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        await ReportAsync(factory, Report("VE", "Caracaz"));
        await ReportAsync(factory, Report("US", "Miamy"));

        var countries = await client.GetFromJsonAsync<List<BillingCountryDto>>($"{Base}/countries", TestContext.Current.CancellationToken);

        Assert.NotNull(countries);
        Assert.Equal(219, countries.Count);
        Assert.Equal([("US", 1), ("VE", 1)], countries.Take(2).Select(country => (country.Code, country.PendingCount)));
        Assert.Equal("Estados Unidos", countries[0].Name);
        Assert.Equal(0, countries[2].PendingCount);
    }

    [Fact]
    public async Task TheCatalogShowsTheCitiesOfOneCountryWithTheirCode()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var cancellationToken = TestContext.Current.CancellationToken;

        var bostons = await client.GetFromJsonAsync<List<BillingCityRefDto>>($"{Base}/catalog?country=us&search=bost", cancellationToken);

        Assert.NotNull(bostons);
        Assert.Equal(
            [("Boston", "Massachusetts", "02119"), ("Boston", "Massachusetts", "2101")],
            bostons.Select(city => (city.Name, city.StateName, city.CityCode)));
        Assert.Equal(50, (await client.GetFromJsonAsync<List<BillingCityRefDto>>($"{Base}/catalog?country=US", cancellationToken))!.Count);
        await AssertProblemAsync(
            await client.GetAsync($"{Base}/catalog", cancellationToken), HttpStatusCode.UnprocessableEntity, "validation.failed");
    }

    [Fact]
    public async Task TheInboxFiltersByCountryAndCarriesTheBestSuggestion()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await ReportAsync(factory, Report("US", "Miamy"));
        await ReportAsync(factory, Report("VE", "Caracaz") with { SeenAt = Seen.AddHours(1) });

        var us = await client.GetFromJsonAsync<UnresolvedForeignCityPageDto>($"{Base}/unresolved?country=US", cancellationToken);
        var all = await client.GetFromJsonAsync<UnresolvedForeignCityPageDto>($"{Base}/unresolved", cancellationToken);

        Assert.NotNull(us);
        Assert.NotNull(all);
        Assert.Equal((1, 1, 25), (us.Total, us.Page, us.PageSize));
        var miamy = Assert.Single(us.Items);
        Assert.Equal(("Miamy", "MIAMY", "Pending", 1, 1), (miamy.Text, miamy.NormalizedText, miamy.Status, miamy.OrderCount, miamy.TenantCount));
        Assert.Equal(new BillingCountryRefDto("US", "Estados Unidos"), miamy.Country);
        Assert.Equal(("Miami", "Florida", "1", 80), (miamy.BestSuggestion?.Name, miamy.BestSuggestion?.StateName, miamy.BestSuggestion?.CityCode, miamy.BestSuggestion?.Similarity));
        Assert.Equal(["Caracaz", "Miamy"], all.Items.Select(item => item.Text));
    }

    [Fact]
    public async Task TheDetailListsTenantsAndUpToThreeSuggestions()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await ReportAsync(factory, Report("US", "Miamy"));
        await ReportAsync(factory, Report("US", "Miamy") with { TenantId = OtherReportTenantId, OrderId = Guid.CreateVersion7() });
        await ReportAsync(factory, Report("US", "Miamy") with { OrderId = Guid.CreateVersion7() });
        var rowId = await RowIdAsync(factory, "US", "Miamy");

        var detail = await client.GetFromJsonAsync<UnresolvedForeignCityDetailDto>($"{Base}/unresolved/{rowId}", cancellationToken);
        var missing = await client.GetAsync($"{Base}/unresolved/{Guid.CreateVersion7()}", cancellationToken);

        Assert.NotNull(detail);
        Assert.Equal((rowId, "Miamy", "Pending", 3, 2), (detail.Id, detail.Text, detail.Status, detail.OrderCount, detail.TenantCount));
        Assert.Equal(
            [(ReportTenantId, 2), (OtherReportTenantId, 1)],
            detail.Tenants.Select(tenant => (tenant.TenantId, tenant.OrderCount)));
        Assert.InRange(detail.Suggestions.Count, 1, 3);
        Assert.Equal(("Miami", "Florida"), (detail.Suggestions[0].Name, detail.Suggestions[0].StateName));
        Assert.Equal(detail.Suggestions[0], detail.BestSuggestion);
        await AssertProblemAsync(missing, HttpStatusCode.NotFound, "geography.unresolved.not_found");
    }

    [Fact]
    public async Task AnUnknownFilterIs422()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var cancellationToken = TestContext.Current.CancellationToken;

        string[] routes =
        [
            $"{Base}/unresolved?status=Resolved",
            $"{Base}/unresolved?pageSize=0",
            $"{Base}/unresolved?country=USA",
            $"{Base}/synonyms?page=0",
            $"{Base}/synonyms?country=U1",
        ];
        foreach (var route in routes)
        {
            await AssertProblemAsync(
                await client.GetAsync(route, cancellationToken), HttpStatusCode.UnprocessableEntity, "validation.failed");
        }
    }

    internal static ForeignCityReport Report(string countryCode, string cityText) =>
        new(ReportTenantId, Guid.CreateVersion7(), countryCode, cityText, Seen);

    internal static async Task ReportAsync(QepApiFactory factory, ForeignCityReport report)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<IForeignCityReporter>()
            .ReportAsync(report, TestContext.Current.CancellationToken);
        Assert.Equal(ForeignCityReportOutcome.Recorded, outcome);
    }

    /// <summary>La fila <c>Pending</c> del país y el texto; falla si no hay una sola.</summary>
    internal static async Task<Guid> RowIdAsync(QepApiFactory factory, string countryCode, string cityText)
    {
        var key = UnresolvedForeignCity.KeyFor(cityText);
        await using var scope = factory.Services.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<GeographyDbContext>().UnresolvedForeignCities
            .AsNoTracking()
            .SingleAsync(
                row => row.CountryCode == countryCode && row.NormalizedText == key && row.Status == UnresolvedCityStatus.Pending,
                TestContext.Current.CancellationToken);
        return row.Id.Value;
    }
}
