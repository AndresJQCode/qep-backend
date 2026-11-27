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

    // Tarea 5: escrituras.

    [Fact]
    public async Task EveryForeignWriteRouteAnswers403WithOnlyRead()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, GeographyOperatorPermissions.CitiesRead);
        var cancellationToken = TestContext.Current.CancellationToken;
        var id = Guid.CreateVersion7();
        var body = JsonContent.Create(new { billingCityId = Guid.CreateVersion7(), expectedVersion = 1 });

        var responses = new[]
        {
            (Route: "POST resolve", Response: await client.PostAsync($"{Base}/unresolved/{id}/resolve", body, cancellationToken)),
            (Route: "POST discard", Response: await client.PostAsync($"{Base}/unresolved/{id}/discard", null, cancellationToken)),
            (Route: "POST restore", Response: await client.PostAsync($"{Base}/unresolved/{id}/restore", null, cancellationToken)),
            (Route: "PUT synonym", Response: await client.PutAsync($"{Base}/synonyms/{id}", body, cancellationToken)),
            (Route: "DELETE synonym", Response: await client.DeleteAsync($"{Base}/synonyms/{id}", cancellationToken)),
        };
        foreach (var (route, response) in responses)
        {
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{route} answered {response.StatusCode}.");
        }
    }

    [Fact]
    public async Task ResolvingCreatesASynonymThatTheNextResolutionUses()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await ReportAsync(factory, Report("US", "Miamy"));
        var rowId = await RowIdAsync(factory, "US", "Miamy");
        var miamiId = await BillingCityIdAsync(factory, "US", "09", "1");

        var response = await client.PostAsJsonAsync($"{Base}/unresolved/{rowId}/resolve", new { billingCityId = miamiId }, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var synonym = await response.Content.ReadFromJsonAsync<ForeignCitySynonymDto>(cancellationToken);
        Assert.NotNull(synonym);
        Assert.Equal(
            ("Miamy", "MIAMY", "US", "Miami", "Florida", "1", 1L),
            (synonym.Text, synonym.NormalizedText, synonym.Country.Code, synonym.City.Name, synonym.City.StateName, synonym.City.CityCode, synonym.Version));
        Assert.Equal(OperatorSubjectId, synonym.CreatedBy.SubjectId);
        Assert.Equal(new BillingCityCodes("Us", "09", "1"), (await ResolveAsync(factory, "US", "Miamy")).Codes);
        var detail = await client.GetFromJsonAsync<UnresolvedForeignCityDetailDto>($"{Base}/unresolved/{rowId}", cancellationToken);
        Assert.Equal(("Resolved", 2L), (detail?.Status, detail?.Version));
        var audit = Assert.Single(await AuditAsync(factory, GeographyAuditActions.ForeignSynonymCreated));
        Assert.Equal(
            (OperatorTenantId, OperatorSubjectId, "foreign_city_synonym", synonym.Id.ToString()),
            (audit.TenantId, audit.ActorId, audit.ResourceType, audit.ResourceId));
    }

    [Fact]
    public async Task ACityOfAnotherCountryIs422NotInCountry()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await ReportAsync(factory, Report("US", "Miamy"));
        var rowId = await RowIdAsync(factory, "US", "Miamy");
        var caracasId = await BillingCityIdByNameAsync(factory, "VE", "Caracas");

        var otherCountry = await client.PostAsJsonAsync($"{Base}/unresolved/{rowId}/resolve", new { billingCityId = caracasId }, cancellationToken);
        var unknown = await client.PostAsJsonAsync($"{Base}/unresolved/{rowId}/resolve", new { billingCityId = Guid.CreateVersion7() }, cancellationToken);
        var absent = await client.PostAsJsonAsync($"{Base}/unresolved/{rowId}/resolve", new { billingCityId = (Guid?)null }, cancellationToken);
        var missingRow = await client.PostAsJsonAsync($"{Base}/unresolved/{Guid.CreateVersion7()}/resolve", new { billingCityId = caracasId }, cancellationToken);

        await AssertProblemAsync(otherCountry, HttpStatusCode.UnprocessableEntity, "geography.billing_city.not_in_country");
        await AssertProblemAsync(unknown, HttpStatusCode.UnprocessableEntity, "geography.billing_city.not_in_country");
        await AssertProblemAsync(absent, HttpStatusCode.UnprocessableEntity, "validation.failed");
        await AssertProblemAsync(missingRow, HttpStatusCode.NotFound, "geography.unresolved.not_found");
        Assert.Equal(0, await SynonymCountAsync(factory));
    }

    // Review Focus 2: un nombre repetido se puede vincular a cualquiera de sus ciudades; uno único no hace falta vincularlo.
    [Fact]
    public async Task ARepeatedNameCanBeResolvedButAUniqueOneIs422MatchesCity()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await ReportAsync(factory, Report("US", "Boston"));
        await ReportAsync(factory, Report("US", "Miami"));
        var bostonRow = await RowIdAsync(factory, "US", "Boston");
        var miamiRow = await RowIdAsync(factory, "US", "Miami");
        var boston2101 = await BillingCityIdByNameAsync(factory, "US", "Boston", "2101");
        var miamiId = await BillingCityIdAsync(factory, "US", "09", "1");

        var repeated = await client.PostAsJsonAsync($"{Base}/unresolved/{bostonRow}/resolve", new { billingCityId = boston2101 }, cancellationToken);
        var unique = await client.PostAsJsonAsync($"{Base}/unresolved/{miamiRow}/resolve", new { billingCityId = miamiId }, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal("2101", (await repeated.Content.ReadFromJsonAsync<ForeignCitySynonymDto>(cancellationToken))!.City.CityCode);
        await AssertProblemAsync(unique, HttpStatusCode.UnprocessableEntity, "geography.synonym.matches_city");
        Assert.Equal(1, await SynonymCountAsync(factory));
    }

    [Fact]
    public async Task ResolvingTwiceIs422NotPending()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await ReportAsync(factory, Report("US", "Miamy"));
        var rowId = await RowIdAsync(factory, "US", "Miamy");
        var miamiId = await BillingCityIdAsync(factory, "US", "09", "1");
        var first = await client.PostAsJsonAsync($"{Base}/unresolved/{rowId}/resolve", new { billingCityId = miamiId }, cancellationToken);

        var second = await client.PostAsJsonAsync($"{Base}/unresolved/{rowId}/resolve", new { billingCityId = miamiId }, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await AssertProblemAsync(second, HttpStatusCode.UnprocessableEntity, "geography.unresolved.not_pending");
        Assert.Equal(1, await SynonymCountAsync(factory));
        Assert.Single(await AuditAsync(factory, GeographyAuditActions.ForeignSynonymCreated));
    }

    [Fact]
    public async Task DiscardAndRestoreFollowTheColombianRules()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await ReportAsync(factory, Report("US", "Miamy"));
        var rowId = await RowIdAsync(factory, "US", "Miamy");

        var discarded = await client.PostAsync($"{Base}/unresolved/{rowId}/discard", null, cancellationToken);
        var discardedAgain = await client.PostAsync($"{Base}/unresolved/{rowId}/discard", null, cancellationToken);
        var restoreMissing = await client.PostAsync($"{Base}/unresolved/{Guid.CreateVersion7()}/restore", null, cancellationToken);
        var restored = await client.PostAsync($"{Base}/unresolved/{rowId}/restore", null, cancellationToken);
        var restoredAgain = await client.PostAsync($"{Base}/unresolved/{rowId}/restore", null, cancellationToken);

        var item = await discarded.Content.ReadFromJsonAsync<UnresolvedForeignCityListItemDto>(cancellationToken);
        Assert.Equal(("Discarded", 2L, 1), (item?.Status, item?.Version, item?.OrderCount));
        await AssertProblemAsync(discardedAgain, HttpStatusCode.UnprocessableEntity, "geography.unresolved.not_pending");
        await AssertProblemAsync(restoreMissing, HttpStatusCode.NotFound, "geography.unresolved.not_found");
        Assert.Equal("Pending", (await restored.Content.ReadFromJsonAsync<UnresolvedForeignCityListItemDto>(cancellationToken))?.Status);
        await AssertProblemAsync(restoredAgain, HttpStatusCode.UnprocessableEntity, "geography.unresolved.not_discarded");
        Assert.Single(await AuditAsync(factory, GeographyAuditActions.UnresolvedForeignCityDiscarded));
        Assert.Single(await AuditAsync(factory, GeographyAuditActions.UnresolvedForeignCityRestored));
    }

    [Fact]
    public async Task CorrectingNeedsTheCurrentVersionAndAnotherCityOfTheCountry()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var cancellationToken = TestContext.Current.CancellationToken;
        var synonym = await ResolvedSynonymAsync(factory, client, "Miamy");
        var orlandoId = await BillingCityIdByNameAsync(factory, "US", "Orlando");
        var caracasId = await BillingCityIdByNameAsync(factory, "VE", "Caracas");
        var miamiId = await BillingCityIdAsync(factory, "US", "09", "1");

        var corrected = await client.PutAsJsonAsync($"{Base}/synonyms/{synonym.Id}", new { billingCityId = orlandoId, expectedVersion = 1 }, cancellationToken);
        var stale = await client.PutAsJsonAsync($"{Base}/synonyms/{synonym.Id}", new { billingCityId = miamiId, expectedVersion = 1 }, cancellationToken);
        var same = await client.PutAsJsonAsync($"{Base}/synonyms/{synonym.Id}", new { billingCityId = orlandoId, expectedVersion = 2 }, cancellationToken);
        var otherCountry = await client.PutAsJsonAsync($"{Base}/synonyms/{synonym.Id}", new { billingCityId = caracasId, expectedVersion = 2 }, cancellationToken);
        var noVersion = await client.PutAsJsonAsync($"{Base}/synonyms/{synonym.Id}", new { billingCityId = miamiId }, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        var dto = await corrected.Content.ReadFromJsonAsync<ForeignCitySynonymDto>(cancellationToken);
        Assert.Equal(("Orlando", 2L), (dto?.City.Name, dto?.Version));
        await AssertProblemAsync(stale, HttpStatusCode.PreconditionFailed, "concurrency.conflict");
        await AssertProblemAsync(same, HttpStatusCode.UnprocessableEntity, "geography.synonym.same_city");
        await AssertProblemAsync(otherCountry, HttpStatusCode.UnprocessableEntity, "geography.billing_city.not_in_country");
        await AssertProblemAsync(noVersion, HttpStatusCode.UnprocessableEntity, "validation.failed");
        Assert.Equal("Orlando", await BillingCityNameAsync(factory, (await ResolveAsync(factory, "US", "Miamy")).Codes!));
        Assert.Single(await AuditAsync(factory, GeographyAuditActions.ForeignSynonymCityChanged));
    }

    [Fact]
    public async Task DeletingIs204ThenNotFoundAndTheTextOpensANewRow()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var cancellationToken = TestContext.Current.CancellationToken;
        var synonym = await ResolvedSynonymAsync(factory, client, "Miamy");

        var deleted = await client.DeleteAsync($"{Base}/synonyms/{synonym.Id}", cancellationToken);
        var again = await client.DeleteAsync($"{Base}/synonyms/{synonym.Id}", cancellationToken);
        await ReportAsync(factory, Report("US", "Miamy") with { OrderId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await AssertProblemAsync(again, HttpStatusCode.NotFound, "geography.synonym.not_found");
        var fresh = await RowIdAsync(factory, "US", "Miamy");
        var detail = await client.GetFromJsonAsync<UnresolvedForeignCityDetailDto>($"{Base}/unresolved/{fresh}", cancellationToken);
        Assert.Equal(("Pending", 1), (detail?.Status, detail?.OrderCount));
        Assert.Single(await AuditAsync(factory, GeographyAuditActions.ForeignSynonymDeleted));
        var page = await client.GetFromJsonAsync<ForeignCitySynonymPageDto>($"{Base}/synonyms", cancellationToken);
        Assert.Empty(page!.Items);
    }

    [Fact]
    public async Task TheSynonymListFiltersByCountryAndSearch()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var cancellationToken = TestContext.Current.CancellationToken;
        var synonym = await ResolvedSynonymAsync(factory, client, "Miamy");

        var us = await client.GetFromJsonAsync<ForeignCitySynonymPageDto>($"{Base}/synonyms?country=US&search=mia", cancellationToken);
        var ve = await client.GetFromJsonAsync<ForeignCitySynonymPageDto>($"{Base}/synonyms?country=VE", cancellationToken);
        var byCity = await client.GetFromJsonAsync<ForeignCitySynonymPageDto>($"{Base}/synonyms?search=miami", cancellationToken);

        Assert.Equal((1, 1, 25), (us!.Total, us.Page, us.PageSize));
        Assert.Equal(synonym.Id, Assert.Single(us.Items).Id);
        Assert.Empty(ve!.Items);
        Assert.Equal(1, byCity!.Total);
    }

    private static async Task<ForeignCitySynonymDto> ResolvedSynonymAsync(QepApiFactory factory, HttpClient client, string text)
    {
        await ReportAsync(factory, Report("US", text));
        var rowId = await RowIdAsync(factory, "US", text);
        var miamiId = await BillingCityIdAsync(factory, "US", "09", "1");
        var response = await client.PostAsJsonAsync(
            $"{Base}/unresolved/{rowId}/resolve", new { billingCityId = miamiId }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ForeignCitySynonymDto>(TestContext.Current.CancellationToken))!;
    }

    // Un scope por llamada, como cada pedido que arma Bootstrapper.
    private static async Task<ForeignCityResolution> ResolveAsync(QepApiFactory factory, string country, string text)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IForeignBillingCityResolver>()
            .ResolveAsync(country, text, TestContext.Current.CancellationToken);
    }

    private static async Task<Guid> BillingCityIdAsync(QepApiFactory factory, string country, string stateCode, string cityCode)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var city = await scope.ServiceProvider.GetRequiredService<GeographyDbContext>().BillingCities.AsNoTracking()
            .SingleAsync(
                city => city.CountryCode == country && city.StateCode == stateCode && city.CityCode == cityCode,
                TestContext.Current.CancellationToken);
        return city.Id.Value;
    }

    private static async Task<Guid> BillingCityIdByNameAsync(
        QepApiFactory factory, string country, string name, string? cityCode = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var city = await scope.ServiceProvider.GetRequiredService<GeographyDbContext>().BillingCities.AsNoTracking()
            .Where(city => city.CountryCode == country && city.CityName == name && (cityCode == null || city.CityCode == cityCode))
            .OrderBy(city => city.StateName).ThenBy(city => city.CityCode)
            .FirstAsync(TestContext.Current.CancellationToken);
        return city.Id.Value;
    }

    private static async Task<string> BillingCityNameAsync(QepApiFactory factory, BillingCityCodes codes)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var city = await scope.ServiceProvider.GetRequiredService<GeographyDbContext>().BillingCities.AsNoTracking()
            .SingleAsync(
                city => city.SiigoCountryCode == codes.SiigoCountryCode && city.StateCode == codes.StateCode && city.CityCode == codes.CityCode,
                TestContext.Current.CancellationToken);
        return city.CityName;
    }

    private static async Task<int> SynonymCountAsync(QepApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<GeographyDbContext>().ForeignCitySynonyms
            .CountAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<List<AuditRow>> AuditAsync(QepApiFactory factory, string action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<GeographyDbContext>().Database.SqlQuery<AuditRow>($"""
            SELECT tenant_id AS "TenantId", actor_id AS "ActorId", resource_type AS "ResourceType", resource_id AS "ResourceId"
            FROM audit.entries
            WHERE source = 'geography' AND action = {action}
            """).ToListAsync(TestContext.Current.CancellationToken);
    }

    private sealed record AuditRow(Guid TenantId, Guid ActorId, string ResourceType, string ResourceId);
}
