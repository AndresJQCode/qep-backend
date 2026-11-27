using System.Net;
using System.Net.Http.Json;
using Modules.Wms.Application;
using Modules.Wms.Domain.Control;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;
using Testcontainers.PostgreSql;
using static Modules.Wms.IntegrationTests.Incidents.IncidentTestSupport;
using static Modules.Wms.IntegrationTests.Tasks.TaskApiSupport;
using static Modules.Wms.IntegrationTests.Tasks.TaskTestSupport;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests.Incidents;

/// <summary>Cuándo <c>AcceptIntoStock</c> de un <c>SyncConflict</c> tiene algo seguro que aplicar (revisión final de T2, M3 y L3): la
/// acción rechazada que apuntó a otro destino, la variante de lotes sin lote y una cantidad mayor que lo que falta de la línea no
/// se ofrecen ni se aceptan: sólo se rechazan.</summary>
public sealed class AcceptIntoStockRulesApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Floor(LedgerSetup Setup, Guid Destination, TaskDto Task, Incident Incident);

    /// <summary>10 en A-01-02, un movimiento de 5 hacia B-01-01 y un <c>SyncConflict</c> por <paramref name="found"/> con el cuerpo
    /// crudo que se indique.</summary>
    private static async Task<Floor> ArrangeAsync(
        PostgreSqlContainer database, QepApiFactory factory, decimal found = 3, Func<Guid, string?>? payload = null, string? tracking = null)
    {
        var s = await WmsLedgerSeeding.ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        if (tracking is not null)
        {
            await SetTrackingAsync(s.Manager, s.VariantId, tracking);
        }
        else
        {
            await StockAsync(factory, s, s.BinA, 10);
        }

        var task = await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination, 5)]));
        var subject = new IncidentSubject(
            task.Id, task.Lines[0].Id, new LocationId(s.BinA), s.VariantId, null, StockStatus.Available, null, found);
        var incident = await NumberAndSaveAsync(
            factory,
            s,
            number => NewIncident(
                s,
                number,
                IncidentType.SyncConflict,
                subject,
                rawPayload: payload?.Invoke(destination),
                reportedBy: Guid.CreateVersion7(),
                operationKey: Guid.NewGuid().ToString()));
        return new Floor(s, destination, task, incident);
    }

    // Otro conflicto sobre la misma línea (el mismo operario reenvía la acción con otra clave).
    private static Task<Incident> AnotherConflictAsync(QepApiFactory factory, Floor floor, decimal found) =>
        NumberAndSaveAsync(
            factory,
            floor.Setup,
            number => NewIncident(
                floor.Setup,
                number,
                IncidentType.SyncConflict,
                new IncidentSubject(
                    floor.Task.Id, floor.Task.Lines[0].Id, new LocationId(floor.Setup.BinA), floor.Setup.VariantId, null,
                    StockStatus.Available, null, found),
                reportedBy: Guid.CreateVersion7(),
                operationKey: Guid.NewGuid().ToString()));

    private static async Task<IncidentDto> GetIncidentAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"{WmsUrl()}/incidents/{id}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IncidentDto>(Ct))!;
    }

    private static Task<HttpResponseMessage> AcceptIntoStockAsync(HttpClient client, Guid id) =>
        client.PostAsJsonAsync($"{WmsUrl()}/incidents/{id}/resolve", new { version = 1, resolution = "AcceptIntoStock" }, Ct);

    private static async Task AssertNotOfferedNorAcceptedAsync(PostgreSqlContainer database, Floor floor)
    {
        var dto = await GetIncidentAsync(floor.Setup.Manager, floor.Incident.Id);
        Assert.DoesNotContain("AcceptIntoStock", dto.AllowedResolutions);
        Assert.True(dto.CanBeRejected);

        var response = await AcceptIntoStockAsync(floor.Setup.Manager, floor.Incident.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.incident.resolution_not_allowed", await CodeOfAsync(response));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.stock_movements WHERE document_type = 'Incident'"));
        Assert.Equal("Open", (await GetIncidentAsync(floor.Setup.Manager, floor.Incident.Id)).Status);
    }

    [Fact]
    public async Task ARejectedActionThatTargetedAnotherDestinationCannotBeAcceptedIntoStock()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeAsync(
            database, factory, payload: _ => $$"""{"action":"confirm","toLocationId":"{{Guid.NewGuid()}}","quantity":3}""");

        await AssertNotOfferedNorAcceptedAsync(database, floor);
    }

    [Fact]
    public async Task ARejectedActionThatTargetedTheLineDestinationIsStillAccepted()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeAsync(
            database, factory, payload: destination => $$"""{"action":"confirm","toLocationId":"{{destination}}","quantity":3}""");

        var dto = await GetIncidentAsync(floor.Setup.Manager, floor.Incident.Id);
        var response = await AcceptIntoStockAsync(floor.Setup.Manager, floor.Incident.Id);

        Assert.Contains("AcceptIntoStock", dto.AllowedResolutions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AConflictOfALotTrackedVariantWithoutLotCannotBeAcceptedIntoStock()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeAsync(database, factory, tracking: "Lot");

        await AssertNotOfferedNorAcceptedAsync(database, floor);
    }

    [Fact]
    public async Task AQuantityBeyondWhatTheLineStillMissesCannotBeAcceptedIntoStock()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeAsync(database, factory, found: 6);

        await AssertNotOfferedNorAcceptedAsync(database, floor);
    }

    [Fact]
    public async Task ConflictsOnOneLineCannotTogetherMoveMoreThanItMisses()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeAsync(database, factory, found: 3);
        var second = await AnotherConflictAsync(factory, floor, 3);

        var first = await AcceptIntoStockAsync(floor.Setup.Manager, floor.Incident.Id);
        var offered = await GetIncidentAsync(floor.Setup.Manager, second.Id);
        var refused = await AcceptIntoStockAsync(floor.Setup.Manager, second.Id);

        // 3 + 3 son 6 y a la línea le faltaban 5: la primera pasa y la segunda ya no se ofrece ni se acepta.
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.DoesNotContain("AcceptIntoStock", offered.AllowedResolutions);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("wms.incident.resolution_not_allowed", await CodeOfAsync(refused));
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.stock_movements WHERE document_type = 'Incident'"));
    }

    [Fact]
    public async Task TwoConcurrentResolvesOnOneLineMoveStockOnlyOnce()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeAsync(database, factory, found: 3);
        var second = await AnotherConflictAsync(factory, floor, 3);

        var responses = await Task.WhenAll(
            AcceptIntoStockAsync(floor.Setup.Manager, floor.Incident.Id), AcceptIntoStockAsync(floor.Setup.Manager, second.Id));

        Assert.DoesNotContain(responses, response => (int)response.StatusCode >= 500);
        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.stock_movements WHERE document_type = 'Incident'"));
    }

    [Fact]
    public async Task AQuantityEqualToWhatTheLineStillMissesIsAccepted()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeAsync(database, factory, found: 5);

        var dto = await GetIncidentAsync(floor.Setup.Manager, floor.Incident.Id);
        var response = await AcceptIntoStockAsync(floor.Setup.Manager, floor.Incident.Id);

        Assert.Contains("AcceptIntoStock", dto.AllowedResolutions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
