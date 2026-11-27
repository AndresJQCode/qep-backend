using System.Net;
using static Modules.Wms.IntegrationTests.Incidents.IncidentTestSupport;
using static Modules.Wms.IntegrationTests.Tasks.TaskApiSupport;
using static Modules.Wms.IntegrationTests.Tasks.TaskCountApiTests;
using static Modules.Wms.IntegrationTests.WmsApiHarness;

namespace Modules.Wms.IntegrationTests.Tasks;

/// <summary>Un <c>short</c> sobre una clave y el <c>count</c> del <c>Count</c> abierto de esa misma clave a la vez (revisión final de
/// T2, L4): el orden de bloqueos (operario → tarea → <c>count-key</c> → <c>Count</c> → numeraciones → saldos) los serializa sin
/// deadlock. Cada uno termina bien o con un 409 limpio, nunca con un 500.</summary>
public sealed class ShortAndCountConcurrencyApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AShortAndTheCountOfTheSameKeyAtTheSameTimeNeverDeadlockOrFail500()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var shortage = await ArrangeShortageAsync(database, factory);
        using var _r = shortage.ReporterClient;
        var (_, counter) = await SeedCounterAsync(factory);
        using var _c = counter;
        var s = shortage.Setup;
        // El contador ya tiene el Count abierto de la clave en curso, y el operario tiene otro movimiento desde la misma ubicación.
        var count = await ReadAsync<Modules.Wms.Application.TaskDto>(await NextRawAsync(counter, s.WarehouseId));
        await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, shortage.Destination, 3)]));
        var move = await ReadAsync<Modules.Wms.Application.TaskDto>(await NextRawAsync(shortage.ReporterClient, s.WarehouseId));

        var results = await Task.WhenAll(
            Task.Run(
                () => ShortRawAsync(
                    shortage.ReporterClient, move.Id, move.Lines[0].Id, ShortBody(move.LeaseToken!.Value, 1), NewKey()),
                Ct),
            Task.Run(
                () => CountRawAsync(
                    counter, count.Id, count.Lines[0].Id, CountBody(count.LeaseToken!.Value, s.BinA, 5), NewKey()),
                Ct));

        foreach (var response in results)
        {
            Assert.True(
                response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
                $"Unexpected {(int)response.StatusCode}: {await BodyAsync(response)}");
        }

        // Nada quedó a medias: la novedad del short existe y apunta a un Count que existe.
        var dangling = await ScalarAsync<long>(
            database,
            """
            SELECT count(*) FROM wms.incidents i
            WHERE i.verification_task_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM wms.tasks t WHERE t.id = i.verification_task_id)
            """);
        Assert.Equal(0L, dangling);
    }
}
