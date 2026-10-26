using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Geography.Infrastructure.Persistence;
using static Modules.Geography.IntegrationTests.GeographyApiHarness;

namespace Modules.Geography.IntegrationTests;

/// <summary>
/// La consola de ciudades del operador de punta a punta (spec 2026-10-26 §6, «Contrato HTTP para el frontend» del plan), con el
/// stub de desarrollo. El tenant operador es simulado (sin fila en <c>tenancy.tenants</c>), como en <c>OperatorConsoleApiTests</c>.
/// Las filas de la bandeja las siembra el reporter real, el mismo que llama la importación de Shopify.
/// </summary>
public sealed class CityOperatorApiTests
{
    private static readonly Guid OperatorTenantId = Guid.Parse("01900000-0000-7000-8000-00000000c0de");
    private static readonly Guid OperatorSubjectId = Guid.Parse("01900000-0000-7000-8000-0000000000aa");
    private static readonly Guid ReportTenantId = Guid.Parse("01900000-0000-7000-8000-0000000000bb");
    private static readonly Guid OtherReportTenantId = Guid.Parse("01900000-0000-7000-8000-0000000000cc");
    private static readonly DateTimeOffset Seen = new(2026, 10, 20, 15, 0, 0, TimeSpan.Zero);

    private static readonly string[] ReadAndManage =
        [GeographyOperatorPermissions.CitiesRead, GeographyOperatorPermissions.CitiesManage];

    private static string Base => $"/api/v1/tenants/{OperatorTenantId}/operator/cities";

    [Fact]
    public async Task ANonOperatorTenantIs403EvenWithThePermission()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        var otherTenantId = Guid.CreateVersion7();
        using var client = StubClient(factory, OperatorSubjectId, otherTenantId, ReadAndManage);

        var response = await client.GetAsync(
            $"/api/v1/tenants/{otherTenantId}/operator/cities/summary", TestContext.Current.CancellationToken);

        // Lo corta la política: el stub ya le quitó los operator.* a un tenant que no es el operador.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Sin la política registrada el síntoma sería 500, no 403. Un id que no existe también es 403: el 403 no depende del id.
    [Fact]
    public async Task EveryReadRouteAnswers403WithoutThePermission()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, "tenancy.settings.read");

        string[] routes = [$"{Base}/summary", $"{Base}/unresolved", $"{Base}/unresolved/{Guid.CreateVersion7()}"];
        foreach (var route in routes)
        {
            var response = await client.GetAsync(route, TestContext.Current.CancellationToken);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{route} answered {response.StatusCode}.");
        }
    }

    [Fact]
    public async Task TheListIsOrderedByLastSeenAndCarriesTheBestSuggestion()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var valle = await DepartmentAsync(factory, "76");
        await ReportAsync(factory, Report(valle, "Calli", Seen));
        await ReportAsync(factory, Report(valle, "Calli", Seen.AddHours(1)) with { TenantId = OtherReportTenantId });
        await ReportAsync(factory, Report(valle, "Xyzwvuq", Seen.AddHours(2)));
        await ReportAsync(factory, Report(valle, "Palmyra", Seen.AddHours(-1)));

        var page = await client.GetFromJsonAsync<UnresolvedCityPageDto>(
            $"{Base}/unresolved?pageSize=2", TestContext.Current.CancellationToken);
        var second = await client.GetFromJsonAsync<UnresolvedCityPageDto>(
            $"{Base}/unresolved?page=2&pageSize=2&status=Pending", TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.NotNull(second);
        Assert.Equal((3, 1, 2), (page.Total, page.Page, page.PageSize));
        Assert.Equal(["XYZWVUQ", "CALLI"], page.Items.Select(item => item.NormalizedText));
        Assert.Equal(["PALMYRA"], second.Items.Select(item => item.NormalizedText));
        Assert.Null(page.Items[0].BestSuggestion);
        var calli = page.Items[1];
        Assert.Equal(("Calli", "Pending", 2, 2), (calli.Text, calli.Status, calli.OrderCount, calli.TenantCount));
        Assert.Equal((Seen, Seen.AddHours(1)), (calli.FirstSeenAt, calli.LastSeenAt));
        Assert.Equal(("CALI", 80), (calli.BestSuggestion?.Name, calli.BestSuggestion?.Similarity));
        Assert.Equal(("VALLE DEL CAUCA", "76"), (calli.Department.Name, calli.Department.DivipolaCode));
    }

    // Una página fuera del rango de filas vuelve vacía pero con la página pedida (la pantalla decide con eso), y el filtro
    // por estado sólo acepta Pending y Discarded.
    [Fact]
    public async Task TheListEchoesTheRequestedPageAndRejectsAResolvedFilter()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var valle = await DepartmentAsync(factory, "76");
        await ReportAsync(factory, Report(valle, "Calli", Seen));

        var page = await client.GetFromJsonAsync<UnresolvedCityPageDto>(
            $"{Base}/unresolved?page=7&search=cal", TestContext.Current.CancellationToken);
        var resolved = await client.GetAsync($"{Base}/unresolved?status=Resolved", TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal((1, 7, 25), (page.Total, page.Page, page.PageSize));
        Assert.Empty(page.Items);
        await AssertProblemAsync(resolved, HttpStatusCode.UnprocessableEntity, "validation.failed");
    }

    [Fact]
    public async Task TheDetailJsonMatchesTheContract()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var valle = await DepartmentAsync(factory, "76");
        var first = await ReportAsync(factory, Report(valle, "Calli", Seen) with { ProvinceCode = "VAC" });
        await ReportAsync(factory, Report(valle, "Calli", Seen.AddHours(1)));
        await ReportAsync(factory, Report(valle, "Calli", Seen.AddHours(2)) with { TenantId = OtherReportTenantId });

        var response = await client.GetAsync(
            $"{Base}/unresolved/{first.UnresolvedCityId!.Value.Value}", TestContext.Current.CancellationToken);
        var missing = await client.GetAsync($"{Base}/unresolved/{Guid.CreateVersion7()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = json.RootElement;
        Assert.Equal(
            ["id", "text", "normalizedText", "department", "status", "sampleAddress", "provinceCode", "orderCount", "tenants",
             "firstSeenAt", "lastSeenAt", "suggestions", "synonymId", "version"],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(["id", "name", "divipolaCode"], root.GetProperty("department").EnumerateObject().Select(p => p.Name));
        Assert.Equal("Pending", root.GetProperty("status").GetString());
        Assert.Equal(3, root.GetProperty("orderCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("synonymId").ValueKind);
        var tenants = root.GetProperty("tenants").EnumerateArray().ToList();
        Assert.Equal(["tenantId", "name", "orderCount"], tenants[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            [(ReportTenantId, 2), (OtherReportTenantId, 1)],
            tenants.Select(tenant => (tenant.GetProperty("tenantId").GetGuid(), tenant.GetProperty("orderCount").GetInt32())));
        var suggestions = root.GetProperty("suggestions").EnumerateArray().ToList();
        Assert.InRange(suggestions.Count, 1, 3);
        Assert.Equal(
            ["cityId", "name", "divipolaCode", "divipolaName", "similarity"],
            suggestions[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal("CALI", suggestions[0].GetProperty("name").GetString());
        Assert.Equal(80, suggestions[0].GetProperty("similarity").GetInt32());
        await AssertProblemAsync(missing, HttpStatusCode.NotFound, "geography.unresolved.not_found");
    }

    [Fact]
    public async Task TheSummaryCountsPendingDiscardedSynonymsAndWaitingOrders()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var valle = await DepartmentAsync(factory, "76");
        await ReportAsync(factory, Report(valle, "Calli", Seen));
        await ReportAsync(factory, Report(valle, "Calli", Seen));
        await ReportAsync(factory, Report(valle, "Palmyra", Seen));
        var discarded = await ReportAsync(factory, Report(valle, "Xyzwvuq", Seen));
        await DiscardAsync(factory, discarded.UnresolvedCityId!.Value);
        await AddSynonymAsync(factory, valle, "Kali");

        var summary = await client.GetFromJsonAsync<CitySummaryDto>($"{Base}/summary", TestContext.Current.CancellationToken);

        Assert.Equal(new CitySummaryDto(PendingCount: 2, DiscardedCount: 1, SynonymCount: 1, OrdersWaitingCount: 3), summary);
    }

    internal static UnresolvedCityReport Report(DepartmentId department, string cityText, DateTimeOffset seenAt) =>
        new(
            ReportTenantId,
            "shopify",
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            null,
            department,
            cityText,
            "Calle 5 # 10-20, Barrio San Fernando",
            null,
            PartyCityOnly: false,
            CustomerCityOnly: false,
            seenAt);

    internal static async Task<UnresolvedCityReportResult> ReportAsync(QepApiFactory factory, UnresolvedCityReport report)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IUnresolvedCityReporter>()
            .ReportAsync(report, TestContext.Current.CancellationToken);
    }

    internal static async Task<DepartmentId> DepartmentAsync(QepApiFactory factory, string code)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        var department = await dbContext.Departments.SingleAsync(
            value => value.DivipolaCode == code, TestContext.Current.CancellationToken);
        return department.Id;
    }

    internal static async Task<City> CityAsync(QepApiFactory factory, string code)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        return await dbContext.Cities.AsNoTracking().SingleAsync(
            value => value.DivipolaCode == code, TestContext.Current.CancellationToken);
    }

    private static async Task DiscardAsync(QepApiFactory factory, UnresolvedCityId id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        var row = await dbContext.UnresolvedCities.SingleAsync(value => value.Id == id, TestContext.Current.CancellationToken);
        row.Discard(Seen.AddDays(1));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task AddSynonymAsync(QepApiFactory factory, DepartmentId department, string text)
    {
        var cali = await CityAsync(factory, "76001");
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICitySynonymRepository>()
            .Add(CitySynonym.Create(CitySynonymId.New(), department, text, cali.Id, OperatorSubjectId, Seen));
        await scope.ServiceProvider.GetRequiredService<IGeographyUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
