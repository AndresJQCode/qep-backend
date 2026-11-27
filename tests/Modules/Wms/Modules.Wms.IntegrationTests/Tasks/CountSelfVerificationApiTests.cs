using System.Net;
using System.Net.Http.Json;
using Modules.Wms.Application;
using static Modules.Wms.IntegrationTests.Tasks.TaskApiSupport;
using static Modules.Wms.IntegrationTests.Tasks.TaskCountApiTests;
using static Modules.Wms.IntegrationTests.WmsApiHarness;

namespace Modules.Wms.IntegrationTests.Tasks;

/// <summary>Decisión del owner, 2026-11-27 (decisión 33): nadie cuenta el <c>Count</c> que verifica una novedad que reportó, ni por
/// <c>next</c>, ni reclamándolo por id, ni porque el supervisor se lo asigne, ni con la acción <c>count</c> (defensa en profundidad).
/// Es <c>wms.incident.self_resolution</c> (422). En una bodega de una persona el conteo espera y el supervisor resuelve a mano.</summary>
public sealed class CountSelfVerificationApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheReporterCannotClaimTheirCountById()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var shortage = await ArrangeShortageAsync(database, factory, reporterCounts: true);
        using var _r = shortage.ReporterClient;

        var response = await ActionRawAsync(shortage.ReporterClient, shortage.CountId, "claim", new { deviceId = "zebra-01" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.incident.self_resolution", await CodeOfAsync(response));
        Assert.Equal("Ready", await ScalarAsync<string>(database, "SELECT status FROM wms.tasks WHERE id = @id", ("id", shortage.CountId)));
    }

    [Fact]
    public async Task AnotherCounterStillClaimsItById()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var shortage = await ArrangeShortageAsync(database, factory);
        using var _r = shortage.ReporterClient;
        var (_, counter) = await SeedCounterAsync(factory);
        using var _c = counter;

        var response = await ActionRawAsync(counter, shortage.CountId, "claim", new { deviceId = "zebra-01" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheSupervisorCannotAssignTheCountToTheReporter()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var shortage = await ArrangeShortageAsync(database, factory, reporterCounts: true);
        using var _r = shortage.ReporterClient;
        var count = await GetAsync(shortage.Setup.Manager, shortage.CountId);

        var assigned = await ActionRawAsync(
            shortage.Setup.Manager, shortage.CountId, "assign", new { memberId = shortage.Reporter, version = count.Version, reason = (string?)null });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, assigned.StatusCode);
        Assert.Equal("wms.incident.self_resolution", await CodeOfAsync(assigned));
        Assert.Null(await ScalarAsync<Guid?>(database, "SELECT assigned_member_id FROM wms.tasks WHERE id = @id", ("id", shortage.CountId)));
    }

    [Fact]
    public async Task TheSupervisorCanAssignTheCountToSomeoneElse()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var shortage = await ArrangeShortageAsync(database, factory);
        using var _r = shortage.ReporterClient;
        var (counterId, counter) = await SeedCounterAsync(factory);
        using var _c = counter;
        var count = await GetAsync(shortage.Setup.Manager, shortage.CountId);

        var assigned = await ActionRawAsync(
            shortage.Setup.Manager, shortage.CountId, "assign", new { memberId = counterId, version = count.Version, reason = (string?)null });

        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
    }

    [Fact]
    public async Task TheCountActionRefusesTheReporterEvenIfTheyHoldTheCount()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var shortage = await ArrangeShortageAsync(database, factory);
        using var _r = shortage.ReporterClient;
        var (_, counter) = await SeedCounterAsync(factory);
        using var _c = counter;
        // El contador ya tiene el Count en curso cuando declara, él mismo, otra novedad sobre la misma clave: queda enlazada a ese Count.
        var count = await ReadAsync<TaskDto>(await NextRawAsync(counter, shortage.Setup.WarehouseId));
        using var report = new HttpRequestMessage(HttpMethod.Post, $"{WmsUrl()}/incidents")
        {
            Content = JsonContent.Create(new
            {
                warehouseId = shortage.Setup.WarehouseId,
                type = "LocationMismatch",
                locationId = shortage.Setup.BinA,
                variantId = shortage.Setup.VariantId,
                quantity = 1,
                note = "Parece que hay otra cosa",
                occurredAt = DateTimeOffset.UtcNow,
                deviceId = "zebra-01",
            }),
        };
        report.Headers.Add("Idempotency-Key", NewKey());
        var reported = await counter.SendAsync(report, Ct);
        Assert.Equal(HttpStatusCode.Created, reported.StatusCode);

        var response = await CountRawAsync(
            counter, count.Id, count.Lines[0].Id, CountBody(count.LeaseToken!.Value, shortage.Setup.BinA, 5), NewKey());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.incident.self_resolution", await CodeOfAsync(response));
        Assert.Equal("InProgress", await ScalarAsync<string>(database, "SELECT status FROM wms.tasks WHERE id = @id", ("id", count.Id)));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.adjustments"));
    }

    private static string NewKey() => Guid.NewGuid().ToString();
}
