using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
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

    // SC-11: confirmar, descartar y restaurar.

    [Fact]
    public async Task ResolvingCreatesTheSynonymMarksTheRowAndPublishesItsOccurrences()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var valle = await DepartmentAsync(factory, "76");
        var cali = await CityAsync(factory, "76001");
        var first = await ReportAsync(factory, Report(valle, "Calli", Seen) with { PartyCityOnly = true });
        var second = await ReportAsync(factory, Report(valle, "CALLI", Seen.AddHours(1)) with { TenantId = OtherReportTenantId });
        var rowId = first.UnresolvedCityId!.Value;

        var response = await ResolveAsync(client, rowId, cali.Id.Value);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var synonym = await response.Content.ReadFromJsonAsync<CitySynonymDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(synonym);
        Assert.Equal(("Calli", "CALLI", 1L), (synonym.Text, synonym.NormalizedText, synonym.Version));
        Assert.Equal(new CityRefDto(cali.Id.Value, "CALI", "76001"), synonym.City);
        Assert.Equal((valle.Value, "76"), (synonym.Department.Id, synonym.Department.DivipolaCode));
        Assert.Equal(OperatorSubjectId, synonym.CreatedBy.SubjectId);
        Assert.Equal(
            (0, 0, 0, 0),
            (synonym.CorrectedOrders, synonym.CorrectedCustomers, synonym.PendingCorrections, synonym.FailedCorrections));

        var detail = await client.GetFromJsonAsync<UnresolvedCityDetailDto>(
            $"{Base}/unresolved/{rowId.Value}", TestContext.Current.CancellationToken);
        Assert.Equal(("Resolved", synonym.Id, 2L), (detail?.Status, detail?.SynonymId, detail?.Version));

        var outbox = Assert.Single(await OutboxAsync(factory));
        Assert.Equal((CitySynonymEventNames.Resolved, synonym.Id.ToString()), (outbox.EventName, outbox.CorrelationId));
        using var payload = JsonDocument.Parse(outbox.Payload);
        Assert.Equal(cali.Id.Value, payload.RootElement.GetProperty("cityId").GetGuid());
        Assert.Equal(OperatorTenantId, payload.RootElement.GetProperty("operatorTenantId").GetGuid());
        Assert.Equal(
            new[] { (ReportTenantId, true), (OtherReportTenantId, false) }.Order(),
            payload.RootElement.GetProperty("occurrences").EnumerateArray()
                .Select(occurrence => (occurrence.GetProperty("tenantId").GetGuid(), occurrence.GetProperty("partyCityOnly").GetBoolean()))
                .Order());
        Assert.Equal(first.UnresolvedCityId, second.UnresolvedCityId);
    }

    // Review Focus 1: el reporter siembra «Calima», que es el nombre de una ciudad real del Valle. Confirmarlo como sinónimo de
    // otra ciudad nunca casaría (el nombre real gana) y se rechaza, en el impacto y al confirmar.
    [Fact]
    public async Task ResolvingATextThatIsARealCityNameIs422()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var valle = await DepartmentAsync(factory, "76");
        var cali = await CityAsync(factory, "76001");
        var row = await ReportAsync(factory, Report(valle, "Calima", Seen));

        var impact = await client.PostAsJsonAsync(
            $"{Base}/unresolved/{row.UnresolvedCityId!.Value.Value}/impact", new { cityId = cali.Id.Value },
            TestContext.Current.CancellationToken);
        var resolve = await ResolveAsync(client, row.UnresolvedCityId.Value, cali.Id.Value);

        await AssertProblemAsync(impact, HttpStatusCode.UnprocessableEntity, "geography.synonym.matches_city");
        await AssertProblemAsync(resolve, HttpStatusCode.UnprocessableEntity, "geography.synonym.matches_city");
        Assert.Equal(UnresolvedCityStatus.Pending, (await RowAsync(factory, row.UnresolvedCityId.Value)).Status);
    }

    [Fact]
    public async Task ACityOfAnotherDepartmentIs422()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var valle = await DepartmentAsync(factory, "76");
        var medellin = await CityAsync(factory, "05001");
        var row = await ReportAsync(factory, Report(valle, "Calli", Seen));

        var otherDepartment = await ResolveAsync(client, row.UnresolvedCityId!.Value, medellin.Id.Value);
        var unknownCity = await ResolveAsync(client, row.UnresolvedCityId.Value, Guid.CreateVersion7());
        var withoutCity = await client.PostAsJsonAsync(
            $"{Base}/unresolved/{row.UnresolvedCityId.Value.Value}/resolve", new { cityId = (Guid?)null },
            TestContext.Current.CancellationToken);
        var missingRow = await ResolveAsync(client, UnresolvedCityId.New(), medellin.Id.Value);

        await AssertProblemAsync(otherDepartment, HttpStatusCode.UnprocessableEntity, "geography.city.not_in_department");
        await AssertProblemAsync(unknownCity, HttpStatusCode.UnprocessableEntity, "geography.city.not_in_department");
        await AssertProblemAsync(withoutCity, HttpStatusCode.UnprocessableEntity, "validation.failed");
        await AssertProblemAsync(missingRow, HttpStatusCode.NotFound, "geography.unresolved.not_found");
    }

    [Fact]
    public async Task ResolvingTwiceIs422NotPending()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var valle = await DepartmentAsync(factory, "76");
        var cali = await CityAsync(factory, "76001");
        var row = await ReportAsync(factory, Report(valle, "Calli", Seen));

        var first = await ResolveAsync(client, row.UnresolvedCityId!.Value, cali.Id.Value);
        var second = await ResolveAsync(client, row.UnresolvedCityId.Value, cali.Id.Value);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await AssertProblemAsync(second, HttpStatusCode.UnprocessableEntity, "geography.unresolved.not_pending");
        Assert.Single(await OutboxAsync(factory));
    }

    // §4.4: el sinónimo casa desde el commit, sin esperar a ningún worker.
    [Fact]
    public async Task TheResolvedTextMatchesRightAway()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var valle = await DepartmentAsync(factory, "76");
        var cali = await CityAsync(factory, "76001");
        var row = await ReportAsync(factory, Report(valle, "Calli", Seen));

        var synonym = await (await ResolveAsync(client, row.UnresolvedCityId!.Value, cali.Id.Value))
            .Content.ReadFromJsonAsync<CitySynonymDto>(TestContext.Current.CancellationToken);

        await using var scope = factory.Services.CreateAsyncScope();
        var match = await scope.ServiceProvider.GetRequiredService<ICityRepository>()
            .MatchByNameAsync(valle, "  calli ", TestContext.Current.CancellationToken);
        Assert.NotNull(match);
        Assert.Equal(cali.Id, match.City.Id);
        Assert.Equal(synonym?.Id, match.SynonymId?.Value);
    }

    [Fact]
    public async Task ResolveLeavesAuditInTheOperatorTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var valle = await DepartmentAsync(factory, "76");
        var cali = await CityAsync(factory, "76001");
        var row = await ReportAsync(factory, Report(valle, "Calli", Seen));

        var synonym = await (await ResolveAsync(client, row.UnresolvedCityId!.Value, cali.Id.Value))
            .Content.ReadFromJsonAsync<CitySynonymDto>(TestContext.Current.CancellationToken);

        var entry = Assert.Single(await AuditAsync(factory));
        Assert.Equal(
            (OperatorTenantId, OperatorSubjectId, "Human", GeographyAuditActions.SynonymCreated, "city_synonym", synonym?.Id.ToString()),
            (entry.TenantId, entry.ActorId, entry.ActorType, entry.Action, entry.ResourceType, entry.ResourceId));
    }

    // P14: un tercero toma el candado del texto, así confirmar y reportar quedan esperándolo a la vez. Gane quien gane, no queda
    // una fila Pending: o el reporte entra antes y su ocurrencia viaja en el evento, o encuentra el sinónimo.
    [Fact]
    public async Task AReportRacingTheResolveNeverLeavesAPendingRow()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var valle = await DepartmentAsync(factory, "76");
        var cali = await CityAsync(factory, "76001");
        var row = await ReportAsync(factory, Report(valle, "Calli", Seen));
        var racing = Report(valle, "Calli", Seen.AddHours(1));
        var cancellationToken = TestContext.Current.CancellationToken;

        Task<HttpResponseMessage> resolve;
        Task<UnresolvedCityReportResult> report;
        await using (var holder = factory.Services.CreateAsyncScope())
        {
            var unitOfWork = holder.ServiceProvider.GetRequiredService<IGeographyUnitOfWork>();
            await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
            await unitOfWork.LockCityTextAsync(valle, "CALLI", cancellationToken);

            resolve = ResolveAsync(client, row.UnresolvedCityId!.Value, cali.Id.Value);
            report = ReportAsync(factory, racing);
            await WaitForAdvisoryLockWaitersAsync(holder.ServiceProvider.GetRequiredService<GeographyDbContext>(), 2);
        }

        await Task.WhenAll(resolve, report);

        Assert.Equal(HttpStatusCode.OK, (await resolve).StatusCode);
        var outcome = (await report).Outcome;
        Assert.Contains(outcome, new[] { UnresolvedCityReportOutcome.Recorded, UnresolvedCityReportOutcome.SynonymExists });
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GeographyDbContext>();
        Assert.DoesNotContain(
            await dbContext.UnresolvedCities.AsNoTracking().ToListAsync(cancellationToken),
            value => value.Status == UnresolvedCityStatus.Pending);
        using var payload = JsonDocument.Parse(Assert.Single(await OutboxAsync(factory)).Payload);
        var sourceRefs = payload.RootElement.GetProperty("occurrences").EnumerateArray()
            .Select(occurrence => occurrence.GetProperty("sourceRef").GetGuid())
            .ToList();
        Assert.Equal(outcome == UnresolvedCityReportOutcome.Recorded, sourceRefs.Contains(racing.SourceRef));
    }

    [Fact]
    public async Task TheImpactNeverWrites()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var valle = await DepartmentAsync(factory, "76");
        var cali = await CityAsync(factory, "76001");
        var row = await ReportAsync(factory, Report(valle, "Calli", Seen) with { PartyCityOnly = true });

        var response = await client.PostAsJsonAsync(
            $"{Base}/unresolved/{row.UnresolvedCityId!.Value.Value}/impact", new { cityId = cali.Id.Value },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            ["city", "ordersToFix", "ordersMissingData", "ordersWithShipping", "ordersNotPending", "customersToComplete",
             "customersStayIncomplete"],
            json.RootElement.EnumerateObject().Select(property => property.Name));
        // El pedido de la ocurrencia no existe en quotations: ningún grupo lo cuenta, pero todos viajan en cero.
        Assert.Equal(0, json.RootElement.GetProperty("ordersToFix").GetProperty("count").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("ordersNotPending").GetProperty("orders").GetArrayLength());
        Assert.Equal("CALI", json.RootElement.GetProperty("city").GetProperty("name").GetString());
        var after = await RowAsync(factory, row.UnresolvedCityId.Value);
        Assert.Equal((UnresolvedCityStatus.Pending, 1L), (after.Status, after.Version));
        Assert.Empty(await OutboxAsync(factory));
        Assert.Empty(await AuditAsync(factory));
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<GeographyDbContext>().CitySynonyms
            .ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DiscardAndRestoreMoveTheRowAndAudit()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        using var readOnly = StubClient(factory, OperatorSubjectId, OperatorTenantId, GeographyOperatorPermissions.CitiesRead);
        var valle = await DepartmentAsync(factory, "76");
        var row = await ReportAsync(factory, Report(valle, "Calli", Seen));
        var route = $"{Base}/unresolved/{row.UnresolvedCityId!.Value.Value}";
        var cancellationToken = TestContext.Current.CancellationToken;

        var forbidden = await readOnly.PostAsync($"{route}/discard", null, cancellationToken);
        var discarded = await client.PostAsync($"{route}/discard", null, cancellationToken);
        var discardedAgain = await client.PostAsync($"{route}/discard", null, cancellationToken);
        var discardedItem = await discarded.Content.ReadFromJsonAsync<UnresolvedCityListItemDto>(cancellationToken);
        var restored = await client.PostAsync($"{route}/restore", null, cancellationToken);
        var restoredAgain = await client.PostAsync($"{route}/restore", null, cancellationToken);
        var restoredItem = await restored.Content.ReadFromJsonAsync<UnresolvedCityListItemDto>(cancellationToken);
        var missing = await client.PostAsync($"{Base}/unresolved/{Guid.CreateVersion7()}/discard", null, cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(("Discarded", 2L, 1), (discardedItem?.Status, discardedItem?.Version, discardedItem?.OrderCount));
        await AssertProblemAsync(discardedAgain, HttpStatusCode.UnprocessableEntity, "geography.unresolved.not_pending");
        Assert.Equal(("Pending", 3L), (restoredItem?.Status, restoredItem?.Version));
        await AssertProblemAsync(restoredAgain, HttpStatusCode.UnprocessableEntity, "geography.unresolved.not_discarded");
        await AssertProblemAsync(missing, HttpStatusCode.NotFound, "geography.unresolved.not_found");
        var audit = await AuditAsync(factory);
        Assert.Equal(
            [GeographyAuditActions.UnresolvedCityDiscarded, GeographyAuditActions.UnresolvedCityRestored],
            audit.OrderBy(entry => entry.OccurredAt).Select(entry => entry.Action));
        Assert.All(audit, entry => Assert.Equal(
            (OperatorTenantId, OperatorSubjectId, row.UnresolvedCityId.Value.Value.ToString()),
            (entry.TenantId, entry.ActorId, entry.ResourceId)));
    }

    // Review SC-10/11: el 403 vale también para el impacto (lectura) y para confirmar y restaurar (escritura), con un id que no
    // existe: el 403 no depende de si el id existe.
    [Fact]
    public async Task TheImpactAndTheWriteRoutesAnswer403WithoutThePermission()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var noPermission = StubClient(factory, OperatorSubjectId, OperatorTenantId, "tenancy.settings.read");
        using var readOnly = StubClient(factory, OperatorSubjectId, OperatorTenantId, GeographyOperatorPermissions.CitiesRead);
        var row = $"{Base}/unresolved/{Guid.CreateVersion7()}";
        var body = new { cityId = Guid.CreateVersion7() };
        var cancellationToken = TestContext.Current.CancellationToken;

        var impact = await noPermission.PostAsJsonAsync($"{row}/impact", body, cancellationToken);
        var resolve = await readOnly.PostAsJsonAsync($"{row}/resolve", body, cancellationToken);
        var restore = await readOnly.PostAsync($"{row}/restore", null, cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, impact.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, resolve.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, restore.StatusCode);
    }

    // Review SC-10/11: confirmar y descartar no llevan expectedVersion en el contrato; el único camino a 412 que tiene el código es
    // la carrera entre leer la fila y guardarla (Version es token de concurrencia). La prueba la provoca de verdad: el decorador de
    // la auditoría —que corre justo antes del guardado— sube la versión de la fila desde otra conexión.
    [Fact]
    public async Task ResolvingARowThatChangedAfterItWasReadIs412()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        var valle = await DepartmentAsync(factory, "76");
        var cali = await CityAsync(factory, "76001");
        var row = await ReportAsync(factory, Report(valle, "Calli", Seen));
        using var racing = RowBumpedBeforeSaving(factory);

        var response = await racing.PostAsJsonAsync(
            $"{Base}/unresolved/{row.UnresolvedCityId!.Value.Value}/resolve", new { cityId = cali.Id.Value },
            TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.PreconditionFailed, "concurrency.conflict");
        Assert.Equal(UnresolvedCityStatus.Pending, (await RowAsync(factory, row.UnresolvedCityId.Value)).Status);
        Assert.Empty(await OutboxAsync(factory));
    }

    [Fact]
    public async Task DiscardingARowThatChangedAfterItWasReadIs412()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        var valle = await DepartmentAsync(factory, "76");
        var row = await ReportAsync(factory, Report(valle, "Calli", Seen));
        using var racing = RowBumpedBeforeSaving(factory);

        var response = await racing.PostAsync(
            $"{Base}/unresolved/{row.UnresolvedCityId!.Value.Value}/discard", null, TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.PreconditionFailed, "concurrency.conflict");
        Assert.Equal(UnresolvedCityStatus.Pending, (await RowAsync(factory, row.UnresolvedCityId.Value)).Status);
    }

    // SC-12: la lista de sinónimos, corregir, borrar y reintentar.

    [Fact]
    public async Task TheSynonymListCarriesCountsEmailAndVersion()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var valle = await DepartmentAsync(factory, "76");
        var cali = await CityAsync(factory, "76001");
        var palmira = await CityAsync(factory, "76520");
        var older = await SeedSynonymAsync(factory, valle, "Kali", cali.Id, Seen);
        var newer = await SeedSynonymAsync(factory, valle, "Palmyra", palmira.Id, Seen.AddHours(1));
        await SeedUserAsync(factory, OperatorSubjectId, "operador@qcode.co");
        // Sin trabajos los cuatro contadores son cero; con ellos, las huellas de los dos orígenes y sólo Pending y Failed.
        await SeedCorrectionAsync(factory, newer, "Correction", "Order");
        await SeedCorrectionAsync(factory, newer, "Import", "Order");
        await SeedCorrectionAsync(factory, newer, "Import", "Customer");
        await SeedJobAsync(factory, newer, "Pending");
        await SeedJobAsync(factory, newer, "Failed");
        await SeedJobAsync(factory, newer, "Failed");
        var cancellationToken = TestContext.Current.CancellationToken;

        var response = await client.GetAsync($"{Base}/synonyms", cancellationToken);
        var echoed = await client.GetFromJsonAsync<CitySynonymPageDto>($"{Base}/synonyms?page=9&search=zzz", cancellationToken);
        // «almir» está en el nombre de la ciudad (PALMIRA) y no en el texto del sinónimo (PALMYRA).
        var byCityName = await client.GetFromJsonAsync<CitySynonymPageDto>($"{Base}/synonyms?search=%20almir", cancellationToken);
        var byText = await client.GetFromJsonAsync<CitySynonymPageDto>($"{Base}/synonyms?search=KALI&pageSize=1", cancellationToken);
        var otherDepartment = await client.GetFromJsonAsync<CitySynonymPageDto>(
            $"{Base}/synonyms?departmentId={Guid.CreateVersion7()}", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(["items", "total", "page", "pageSize"], json.RootElement.EnumerateObject().Select(property => property.Name));
        var items = json.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(
            ["id", "text", "normalizedText", "department", "city", "correctedOrders", "correctedCustomers", "pendingCorrections",
             "failedCorrections", "createdBy", "createdAt", "updatedAt", "version"],
            items[0].EnumerateObject().Select(property => property.Name));
        Assert.Equal(["subjectId", "email"], items[0].GetProperty("createdBy").EnumerateObject().Select(p => p.Name));
        Assert.Equal((2, 1, 25), (json.RootElement.GetProperty("total").GetInt32(), json.RootElement.GetProperty("page").GetInt32(), json.RootElement.GetProperty("pageSize").GetInt32()));

        // updatedAt descendente: el sinónimo más nuevo primero.
        var page = await response.Content.ReadFromJsonAsync<CitySynonymPageDto>(cancellationToken);
        Assert.NotNull(page);
        Assert.Equal([newer.Value, older.Value], page.Items.Select(item => item.Id));
        var withJobs = page.Items[0];
        Assert.Equal(("Palmyra", "PALMYRA", 1L), (withJobs.Text, withJobs.NormalizedText, withJobs.Version));
        Assert.Equal(
            (2, 1, 1, 2), (withJobs.CorrectedOrders, withJobs.CorrectedCustomers, withJobs.PendingCorrections, withJobs.FailedCorrections));
        Assert.Equal(new CitySynonymActorDto(OperatorSubjectId, "operador@qcode.co"), withJobs.CreatedBy);
        Assert.Equal(("PALMIRA", "76520"), (withJobs.City.Name, withJobs.City.DivipolaCode));
        Assert.Equal((valle.Value, "76"), (withJobs.Department.Id, withJobs.Department.DivipolaCode));
        var withoutJobs = page.Items[1];
        Assert.Equal(
            (0, 0, 0, 0),
            (withoutJobs.CorrectedOrders, withoutJobs.CorrectedCustomers, withoutJobs.PendingCorrections, withoutJobs.FailedCorrections));

        // La página pedida se devuelve tal cual, también fuera de rango; la búsqueda mira el texto normalizado o el nombre de la ciudad.
        Assert.NotNull(echoed);
        Assert.Equal((0, 9, 25), (echoed.Total, echoed.Page, echoed.PageSize));
        Assert.Empty(echoed.Items);
        Assert.NotNull(byCityName);
        Assert.Equal([newer.Value], byCityName.Items.Select(item => item.Id));
        Assert.NotNull(byText);
        Assert.Equal([older.Value], byText.Items.Select(item => item.Id));
        Assert.Equal((1, 1), (byText.Total, byText.PageSize));
        Assert.NotNull(otherDepartment);
        Assert.Equal(0, otherDepartment.Total);
    }

    [Fact]
    public async Task TheSynonymListRejectsOutOfRangeValuesWith422()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var cancellationToken = TestContext.Current.CancellationToken;

        var zero = await client.GetAsync($"{Base}/synonyms?page=0", cancellationToken);
        var huge = await client.GetAsync($"{Base}/synonyms?pageSize=101", cancellationToken);
        var longSearch = await client.GetAsync($"{Base}/synonyms?search={new string('a', 101)}", cancellationToken);

        await AssertProblemAsync(zero, HttpStatusCode.UnprocessableEntity, "validation.failed");
        await AssertProblemAsync(huge, HttpStatusCode.UnprocessableEntity, "validation.failed");
        await AssertProblemAsync(longSearch, HttpStatusCode.UnprocessableEntity, "validation.failed");
    }

    [Fact]
    public async Task CorrectingWithAStaleVersionIs412()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var valle = await DepartmentAsync(factory, "76");
        var cali = await CityAsync(factory, "76001");
        var palmira = await CityAsync(factory, "76520");
        var synonym = await SeedSynonymAsync(factory, valle, "Kali", cali.Id, Seen);

        var stale = await CorrectAsync(client, synonym, palmira.Id.Value, expectedVersion: 7);
        var missingVersion = await client.PutAsJsonAsync(
            $"{Base}/synonyms/{synonym.Value}", new { cityId = palmira.Id.Value }, TestContext.Current.CancellationToken);

        await AssertProblemAsync(stale, HttpStatusCode.PreconditionFailed, "concurrency.conflict");
        await AssertProblemAsync(missingVersion, HttpStatusCode.UnprocessableEntity, "validation.failed");
        Assert.Equal((cali.Id, 1L), await SynonymStateAsync(factory, synonym));
        Assert.Empty(await OutboxAsync(factory));
    }

    [Fact]
    public async Task CorrectingToTheSameCityIs422SameCity()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var valle = await DepartmentAsync(factory, "76");
        var cali = await CityAsync(factory, "76001");
        var medellin = await CityAsync(factory, "05001");
        var synonym = await SeedSynonymAsync(factory, valle, "Kali", cali.Id, Seen);

        var same = await CorrectAsync(client, synonym, cali.Id.Value, expectedVersion: 1);
        var sameImpact = await ImpactAsync(client, synonym, cali.Id.Value);
        var otherDepartment = await CorrectAsync(client, synonym, medellin.Id.Value, expectedVersion: 1);
        var unknownCity = await CorrectAsync(client, synonym, Guid.CreateVersion7(), expectedVersion: 1);
        var withoutCity = await client.PutAsJsonAsync(
            $"{Base}/synonyms/{synonym.Value}", new { expectedVersion = 1 }, TestContext.Current.CancellationToken);

        await AssertProblemAsync(same, HttpStatusCode.UnprocessableEntity, "geography.synonym.same_city");
        await AssertProblemAsync(sameImpact, HttpStatusCode.UnprocessableEntity, "geography.synonym.same_city");
        await AssertProblemAsync(otherDepartment, HttpStatusCode.UnprocessableEntity, "geography.city.not_in_department");
        await AssertProblemAsync(unknownCity, HttpStatusCode.UnprocessableEntity, "geography.city.not_in_department");
        await AssertProblemAsync(withoutCity, HttpStatusCode.UnprocessableEntity, "validation.failed");
        Assert.Equal((cali.Id, 1L), await SynonymStateAsync(factory, synonym));
        Assert.Empty(await OutboxAsync(factory));
    }

    // El literal de «Contrato de eventos» del plan: lo que Geography escribe aquí es lo que Shopify lee por nombre
    // (CitySynonymEventPayloads). Los nombres, su orden y los valores, con la ciudad anterior.
    [Fact]
    public async Task CorrectingPublishesTheCorrectedEventWithThePreviousCity()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var valle = await DepartmentAsync(factory, "76");
        var cali = await CityAsync(factory, "76001");
        var palmira = await CityAsync(factory, "76520");
        var synonym = await SeedSynonymAsync(factory, valle, "Kali", cali.Id, Seen);

        var response = await CorrectAsync(client, synonym, palmira.Id.Value, expectedVersion: 1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var corrected = await response.Content.ReadFromJsonAsync<CitySynonymDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(corrected);
        Assert.Equal((synonym.Value, "Kali", "KALI", 2L), (corrected.Id, corrected.Text, corrected.NormalizedText, corrected.Version));
        Assert.Equal(new CityRefDto(palmira.Id.Value, "PALMIRA", "76520"), corrected.City);
        Assert.Equal(OperatorSubjectId, corrected.CreatedBy.SubjectId);
        Assert.Equal((palmira.Id, 2L), await SynonymStateAsync(factory, synonym));

        var outbox = Assert.Single(await OutboxAsync(factory));
        Assert.Equal((CitySynonymEventNames.Corrected, synonym.Value.ToString()), (outbox.EventName, outbox.CorrelationId));
        using var payload = JsonDocument.Parse(outbox.Payload);
        var root = payload.RootElement;
        Assert.Equal(
            ["synonymId", "departmentId", "previousCityId", "cityId", "operatorSubjectId", "operatorTenantId", "occurredAt"],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            (synonym.Value, valle.Value, cali.Id.Value, palmira.Id.Value, OperatorSubjectId, OperatorTenantId),
            (root.GetProperty("synonymId").GetGuid(), root.GetProperty("departmentId").GetGuid(),
             root.GetProperty("previousCityId").GetGuid(), root.GetProperty("cityId").GetGuid(),
             root.GetProperty("operatorSubjectId").GetGuid(), root.GetProperty("operatorTenantId").GetGuid()));
        Assert.True(root.GetProperty("occurredAt").TryGetDateTimeOffset(out _));

        var entry = Assert.Single(await AuditAsync(factory));
        Assert.Equal(
            (OperatorTenantId, OperatorSubjectId, "Human", GeographyAuditActions.SynonymCityChanged, "city_synonym", synonym.Value.ToString()),
            (entry.TenantId, entry.ActorId, entry.ActorType, entry.Action, entry.ResourceType, entry.ResourceId));
    }

    // Impacto de corregir (no escribe): la forma del contrato con todos los grupos, también en cero.
    [Fact]
    public async Task TheCorrectImpactCarriesTheContractShapeAndNeverWrites()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var valle = await DepartmentAsync(factory, "76");
        var cali = await CityAsync(factory, "76001");
        var palmira = await CityAsync(factory, "76520");
        var synonym = await SeedSynonymAsync(factory, valle, "Kali", cali.Id, Seen);
        // Una huella de cliente cuyo cliente ya no existe: la lectura no distingue «editado» de «ya no está», y en los dos casos no
        // se toca, así que cuenta como editado. Una de pedido sin pedido no cuenta en ningún grupo.
        await SeedCorrectionAsync(factory, synonym, "Correction", "Customer");
        await SeedCorrectionAsync(factory, synonym, "Correction", "Order");

        var response = await ImpactAsync(client, synonym, palmira.Id.Value);
        var missing = await ImpactAsync(client, new CitySynonymId(Guid.CreateVersion7()), palmira.Id.Value);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = json.RootElement;
        Assert.Equal(
            ["from", "to", "ordersToChange", "ordersEdited", "ordersNotPending", "customersToChange", "customersEdited",
             "occurrencesUncorrected"],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(("CALI", "PALMIRA"), (root.GetProperty("from").GetProperty("name").GetString(), root.GetProperty("to").GetProperty("name").GetString()));
        foreach (var group in new[] { "ordersToChange", "ordersEdited", "customersToChange", "occurrencesUncorrected" })
        {
            Assert.Equal(0, root.GetProperty(group).GetProperty("count").GetInt32());
            Assert.Equal(0, root.GetProperty(group).GetProperty("byTenant").GetArrayLength());
        }

        Assert.Equal(0, root.GetProperty("ordersNotPending").GetProperty("orders").GetArrayLength());
        Assert.Equal(1, root.GetProperty("customersEdited").GetProperty("count").GetInt32());
        await AssertProblemAsync(missing, HttpStatusCode.NotFound, "geography.synonym.not_found");
        Assert.Equal((cali.Id, 1L), await SynonymStateAsync(factory, synonym));
        Assert.Empty(await OutboxAsync(factory));
        Assert.Empty(await AuditAsync(factory));
    }

    // O6 y §4.7: borrar deja de casar desde el commit, no publica nada y el próximo reporte del texto abre una fila Pending nueva
    // (la Resolved vieja queda fuera del índice parcial, intacta).
    [Fact]
    public async Task DeletingStopsMatchingAndTheNextReportOpensAPendingRow()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var valle = await DepartmentAsync(factory, "76");
        var cali = await CityAsync(factory, "76001");
        var resolvedRow = await ReportAsync(factory, Report(valle, "Calli", Seen));
        var synonym = await (await ResolveAsync(client, resolvedRow.UnresolvedCityId!.Value, cali.Id.Value))
            .Content.ReadFromJsonAsync<CitySynonymDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(synonym);
        var cancellationToken = TestContext.Current.CancellationToken;

        var response = await client.DeleteAsync($"{Base}/synonyms/{synonym.Id}", cancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.Null(await scope.ServiceProvider.GetRequiredService<ICityRepository>().MatchByNameAsync(valle, "calli", cancellationToken));
        }

        var next = await ReportAsync(factory, Report(valle, "Calli", Seen.AddDays(1)));
        Assert.Equal(UnresolvedCityReportOutcome.Recorded, next.Outcome);
        Assert.NotEqual(resolvedRow.UnresolvedCityId, next.UnresolvedCityId);
        Assert.Equal(UnresolvedCityStatus.Pending, (await RowAsync(factory, next.UnresolvedCityId!.Value)).Status);
        Assert.Equal(UnresolvedCityStatus.Resolved, (await RowAsync(factory, resolvedRow.UnresolvedCityId.Value)).Status);

        // No publica nada (sólo el evento de confirmarlo) y lo audita en el tenant operador.
        Assert.Equal([CitySynonymEventNames.Resolved], (await OutboxAsync(factory)).Select(message => message.EventName));
        var deleted = Assert.Single(await AuditAsync(factory), entry => entry.Action == GeographyAuditActions.SynonymDeleted);
        Assert.Equal(
            (OperatorTenantId, OperatorSubjectId, "city_synonym", synonym.Id.ToString()),
            (deleted.TenantId, deleted.ActorId, deleted.ResourceType, deleted.ResourceId));
        var page = await client.GetFromJsonAsync<CitySynonymPageDto>($"{Base}/synonyms", cancellationToken);
        Assert.Empty(page!.Items);
    }

    [Fact]
    public async Task ADeletedSynonymIs404OnEveryRoute()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var valle = await DepartmentAsync(factory, "76");
        var cali = await CityAsync(factory, "76001");
        var palmira = await CityAsync(factory, "76520");
        var synonym = await SeedSynonymAsync(factory, valle, "Kali", cali.Id, Seen);
        var cancellationToken = TestContext.Current.CancellationToken;
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Base}/synonyms/{synonym.Value}", cancellationToken)).StatusCode);

        var correct = await CorrectAsync(client, synonym, palmira.Id.Value, expectedVersion: 2);
        var impact = await ImpactAsync(client, synonym, palmira.Id.Value);
        var retry = await client.PostAsync($"{Base}/synonyms/{synonym.Value}/retry", null, cancellationToken);
        var deleteAgain = await client.DeleteAsync($"{Base}/synonyms/{synonym.Value}", cancellationToken);

        foreach (var response in new[] { correct, impact, retry, deleteAgain })
        {
            await AssertProblemAsync(response, HttpStatusCode.NotFound, "geography.synonym.not_found");
        }
    }

    // Un 403 nunca depende de si el id existe: ids que no existen, y sin el permiso de lectura (lista e impacto) o de escritura
    // (corregir, borrar, reintentar y el recorrido).
    [Fact]
    public async Task EverySynonymRouteAnswers403WithoutThePermission()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var noPermission = StubClient(factory, OperatorSubjectId, OperatorTenantId, "tenancy.settings.read");
        using var readOnly = StubClient(factory, OperatorSubjectId, OperatorTenantId, GeographyOperatorPermissions.CitiesRead);
        var synonym = $"{Base}/synonyms/{Guid.CreateVersion7()}";
        var body = new { cityId = Guid.CreateVersion7(), expectedVersion = 1 };
        var cancellationToken = TestContext.Current.CancellationToken;

        var responses = new[]
        {
            await noPermission.GetAsync($"{Base}/synonyms", cancellationToken),
            await noPermission.PostAsJsonAsync($"{synonym}/impact", body, cancellationToken),
            await readOnly.PutAsJsonAsync(synonym, body, cancellationToken),
            await readOnly.DeleteAsync(synonym, cancellationToken),
            await readOnly.PostAsync($"{synonym}/retry", null, cancellationToken),
            await readOnly.PostAsync($"{Base}/backfill", null, cancellationToken),
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
    }

    // D-C6: los trabajos Failed del sinónimo, sembrados por SQL, vuelven a Pending sin intentos, lease ni error; los de otro
    // sinónimo y los que no estaban Failed no se tocan. La auditoría lleva cuántos eran.
    [Fact]
    public async Task RetryMovesFailedJobsBackToPending()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), OperatorTenantId);
        using var client = StubClient(factory, OperatorSubjectId, OperatorTenantId, ReadAndManage);
        var valle = await DepartmentAsync(factory, "76");
        var cali = await CityAsync(factory, "76001");
        var palmira = await CityAsync(factory, "76520");
        var synonym = await SeedSynonymAsync(factory, valle, "Kali", cali.Id, Seen);
        var other = await SeedSynonymAsync(factory, valle, "Palmyra", palmira.Id, Seen);
        var failed = await SeedJobAsync(factory, synonym, "Failed");
        var alreadyPending = await SeedJobAsync(factory, synonym, "Pending");
        var skipped = await SeedJobAsync(factory, synonym, "Skipped");
        var othersFailed = await SeedJobAsync(factory, other, "Failed");

        var response = await client.PostAsync($"{Base}/synonyms/{synonym.Value}/retry", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<CitySynonymDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(dto);
        Assert.Equal((synonym.Value, 2, 0), (dto.Id, dto.PendingCorrections, dto.FailedCorrections));
        Assert.Equal("Pending|0|none|none|none", await JobAsync(factory, failed));
        Assert.Equal("Pending|0|none|none|none", await JobAsync(factory, alreadyPending));
        Assert.Equal("Skipped", (await JobAsync(factory, skipped))[..7]);
        Assert.Equal("Failed|5|boom|lease|closed", await JobAsync(factory, othersFailed));
        var entry = Assert.Single(await AuditAsync(factory));
        Assert.Equal(
            (OperatorTenantId, GeographyAuditActions.SynonymCorrectionsRetried, "city_synonym", synonym.Value.ToString()),
            (entry.TenantId, entry.Action, entry.ResourceType, entry.ResourceId));
        // Repetirlo sin fallidos no cambia nada.
        var again = await client.PostAsync($"{Base}/synonyms/{synonym.Value}/retry", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    private static Task<HttpResponseMessage> CorrectAsync(HttpClient client, CitySynonymId id, Guid cityId, long expectedVersion) =>
        client.PutAsJsonAsync($"{Base}/synonyms/{id.Value}", new { cityId, expectedVersion }, TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> ImpactAsync(HttpClient client, CitySynonymId id, Guid cityId) =>
        client.PostAsJsonAsync($"{Base}/synonyms/{id.Value}/impact", new { cityId }, TestContext.Current.CancellationToken);

    /// <summary>Un cliente del stub sobre un host cuyo registro de auditoría sube la versión de las filas de la bandeja justo antes
    /// del guardado, desde otra conexión que confirma en el acto: la fila cambia entre que el handler la lee y la guarda.</summary>
    private static HttpClient RowBumpedBeforeSaving(QepApiFactory factory)
    {
        var racing = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            var original = services.Single(descriptor => descriptor.ServiceType == typeof(IGeographyAuditRecorder));
            services.Remove(original);
            services.AddScoped<IGeographyAuditRecorder>(provider => new VersionBumpingAuditRecorder(
                (IGeographyAuditRecorder)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!),
                provider.GetRequiredService<IServiceScopeFactory>()));
        }));
        var client = racing.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", OperatorSubjectId.ToString());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", OperatorTenantId.ToString());
        client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', ReadAndManage));
        return client;
    }

    private sealed class VersionBumpingAuditRecorder(IGeographyAuditRecorder inner, IServiceScopeFactory scopes) : IGeographyAuditRecorder
    {
        public void Record(
            Guid tenantId, Guid actorId, Modules.Audit.Domain.AuditActorType actorType, string action, string resourceType,
            string resourceId, string changedFieldsJson, DateTimeOffset occurredAt)
        {
            using (var scope = scopes.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<GeographyDbContext>().Database
                    .ExecuteSql($"UPDATE geography.unresolved_cities SET version = version + 1");
            }

            inner.Record(tenantId, actorId, actorType, action, resourceType, resourceId, changedFieldsJson, occurredAt);
        }
    }

    private static async Task<CitySynonymId> SeedSynonymAsync(
        QepApiFactory factory, DepartmentId department, string text, CityId city, DateTimeOffset at)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var synonym = CitySynonym.Create(CitySynonymId.New(), department, text, city, OperatorSubjectId, at);
        scope.ServiceProvider.GetRequiredService<ICitySynonymRepository>().Add(synonym);
        await scope.ServiceProvider.GetRequiredService<IGeographyUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        return synonym.Id;
    }

    /// <summary>La ciudad y la versión del sinónimo en la base.</summary>
    private static async Task<(CityId City, long Version)> SynonymStateAsync(QepApiFactory factory, CitySynonymId id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var synonym = await scope.ServiceProvider.GetRequiredService<GeographyDbContext>().CitySynonyms.AsNoTracking()
            .SingleAsync(value => value.Id == id, TestContext.Current.CancellationToken);
        return (synonym.CityId, synonym.Version);
    }

    private static async Task SeedUserAsync(QepApiFactory factory, Guid userId, string email)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<GeographyDbContext>().Database.ExecuteSqlAsync(
            $"""
            INSERT INTO identity.users (id, email, status, created_at, updated_at)
            VALUES ({userId}, {email}, 'Invited', now(), now())
            """,
            TestContext.Current.CancellationToken);
    }

    private static async Task SeedCorrectionAsync(QepApiFactory factory, CitySynonymId synonym, string origin, string target)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<GeographyDbContext>().Database.ExecuteSqlAsync(
            $"""
            INSERT INTO shopify.city_corrections (id, tenant_id, synonym_id, origin, target, target_id, department_id, city_id, applied_at, updated_at)
            VALUES ({Guid.CreateVersion7()}, {ReportTenantId}, {synonym.Value}, {origin}, {target}, {Guid.CreateVersion7()},
                    {Guid.CreateVersion7()}, {Guid.CreateVersion7()}, now(), now())
            """,
            TestContext.Current.CancellationToken);
    }

    /// <summary>Un trabajo de corrección del sinónimo. <c>Failed</c> nace como lo deja el worker al agotar los intentos (5 intentos,
    /// error, lease y cierre); los demás, sin nada de eso.</summary>
    private static async Task<Guid> SeedJobAsync(QepApiFactory factory, CitySynonymId synonym, string status)
    {
        var id = Guid.CreateVersion7();
        var failed = status == "Failed";
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<GeographyDbContext>().Database.ExecuteSqlAsync(
            $"""
            INSERT INTO shopify.city_correction_jobs
                (id, tenant_id, synonym_id, kind, target, target_id, operator_subject_id, status, attempts, last_error, lease_until,
                 processed_at, created_at)
            VALUES ({id}, {ReportTenantId}, {synonym.Value}, 'Resolve', 'Order', {Guid.CreateVersion7()}, {OperatorSubjectId}, {status},
                    CASE WHEN {failed} THEN 5 ELSE 0 END,
                    CASE WHEN {failed} THEN 'boom' END,
                    CASE WHEN {failed} THEN now() END,
                    CASE WHEN {failed} OR {status} = 'Skipped' THEN now() END,
                    now())
            """,
            TestContext.Current.CancellationToken);
        return id;
    }

    /// <summary><c>status|attempts|last_error|lease|processed_at</c>, con <c>none</c> si es nulo, <c>lease</c> y <c>closed</c> si no.</summary>
    private static async Task<string> JobAsync(QepApiFactory factory, Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<GeographyDbContext>().Database.SqlQuery<string>(
            $"""
            SELECT status || '|' || attempts || '|' || coalesce(last_error, 'none') || '|'
                   || CASE WHEN lease_until IS NULL THEN 'none' ELSE 'lease' END || '|'
                   || CASE WHEN processed_at IS NULL THEN 'none' ELSE 'closed' END AS "Value"
            FROM shopify.city_correction_jobs WHERE id = {id}
            """).SingleAsync(TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> ResolveAsync(HttpClient client, UnresolvedCityId id, Guid cityId) =>
        client.PostAsJsonAsync($"{Base}/unresolved/{id.Value}/resolve", new { cityId }, TestContext.Current.CancellationToken);

    private static async Task<UnresolvedCity> RowAsync(QepApiFactory factory, UnresolvedCityId id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<GeographyDbContext>().UnresolvedCities.AsNoTracking()
            .SingleAsync(value => value.Id == id, TestContext.Current.CancellationToken);
    }

    private static async Task<List<OutboxRow>> OutboxAsync(QepApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<GeographyDbContext>().Database.SqlQuery<OutboxRow>($"""
            SELECT event_name AS "EventName", correlation_id AS "CorrelationId", payload::text AS "Payload"
            FROM platform.outbox_messages
            WHERE event_name LIKE 'geography.%'
            """).ToListAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<List<AuditRow>> AuditAsync(QepApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<GeographyDbContext>().Database.SqlQuery<AuditRow>($"""
            SELECT tenant_id AS "TenantId", actor_id AS "ActorId", actor_type AS "ActorType", action AS "Action",
                   resource_type AS "ResourceType", resource_id AS "ResourceId", occurred_at AS "OccurredAt"
            FROM audit.entries
            WHERE source = 'geography'
            """).ToListAsync(TestContext.Current.CancellationToken);
    }

    private static async Task WaitForAdvisoryLockWaitersAsync(GeographyDbContext dbContext, int expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            var waiting = await dbContext.Database
                .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_locks WHERE locktype = 'advisory' AND NOT granted")
                .SingleAsync(TestContext.Current.CancellationToken);
            if (waiting == expected)
            {
                return;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, $"Expected {expected} waiting on the lock, saw {waiting}.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private sealed record OutboxRow(string EventName, string CorrelationId, string Payload);

    private sealed record AuditRow(
        Guid TenantId, Guid ActorId, string ActorType, string Action, string ResourceType, string ResourceId, DateTimeOffset OccurredAt);

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
