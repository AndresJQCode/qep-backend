using System.Net;
using System.Text.Json;
using Modules.Wms.Application;
using Testcontainers.PostgreSql;
using static Modules.Wms.IntegrationTests.Incidents.IncidentTestSupport;
using static Modules.Wms.IntegrationTests.Tasks.TaskApiSupport;
using static Modules.Wms.IntegrationTests.Tasks.TaskTestSupport;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests.Tasks;

/// <summary>El conteo ciego de verificación (spec 2026-11-26 §6.2 y §8.4, DA-39 a DA-47): lo genera una novedad, lo cuenta otra
/// persona sin ver lo esperado, y al cerrarse resuelve las novedades o propone el ajuste.</summary>
public sealed class TaskCountApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] KnownQuantityFields =
        ["quantityConfirmed", "quantityCounted", "quantityPlanned", "quantityRemaining"];

    internal static string NewKey() => Guid.NewGuid().ToString();

    /// <summary>10 en A-01-02, un movimiento de 5 hacia B-01-01 y un faltante declarado por el operario (encontró
    /// <c>found</c>): queda un <c>Count</c> listo sobre A-01-02. El sistema tiene <c>10 - found</c> ahí.</summary>
    internal sealed record Shortage(
        LedgerSetup Setup, Guid Destination, TaskDto Move, Guid Reporter, HttpClient ReporterClient, Guid CountId, Guid IncidentId);

    internal static async Task<Shortage> ArrangeShortageAsync(
        PostgreSqlContainer database, QepApiFactory factory, decimal found = 2, bool reporterCounts = false)
    {
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        await StockAsync(factory, s, s.BinA, 10);
        var move = await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination, 5)]));
        var (reporter, client) = reporterCounts
            ? await SeedMemberAsync(
                factory, OperatorSubject, WmsPermissions.DocumentRead, WmsPermissions.PutawayPerform, WmsPermissions.CountPerform)
            : await SeedOperatorAsync(factory);
        var next = await ReadAsync<TaskDto>(await NextRawAsync(client, s.WarehouseId));
        var response = await ShortRawAsync(
            client, next.Id, next.Lines[0].Id, ShortBody(next.LeaseToken!.Value, found), NewKey());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var countId = await ScalarAsync<Guid>(database, "SELECT id FROM wms.tasks WHERE type = 'Count'");
        var incidentId = await ScalarAsync<Guid>(
            database, "SELECT id FROM wms.incidents WHERE source_task_id = @task", ("task", move.Id));
        return new Shortage(s, destination, move, reporter, client, countId, incidentId);
    }

    [Fact]
    public async Task TheReporterIsNotOfferedTheirCountButAnotherCounterIs()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var shortage = await ArrangeShortageAsync(database, factory, reporterCounts: true);
        using var _r = shortage.ReporterClient;
        var (_, counter) = await SeedCounterAsync(factory);
        using var _c = counter;

        // DA-45: la persona que declaró el faltante también tiene CountPerform, y aun así no recibe el conteo que lo verifica.
        var reporterNext = await NextRawAsync(shortage.ReporterClient, shortage.Setup.WarehouseId);
        var counterNext = await NextRawAsync(counter, shortage.Setup.WarehouseId);

        Assert.Equal(HttpStatusCode.NoContent, reporterNext.StatusCode);
        Assert.Equal(HttpStatusCode.OK, counterNext.StatusCode);
        var count = await ReadAsync<TaskDto>(counterNext);
        Assert.Equal((shortage.CountId, "Count", "InProgress"), (count.Id, count.Type, count.Status));
    }

    [Fact]
    public async Task TheCountDtoRevealsNothingExpected()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var shortage = await ArrangeShortageAsync(database, factory);
        using var _r = shortage.ReporterClient;
        var (_, counter) = await SeedCounterAsync(factory);
        using var _c = counter;
        var response = await NextRawAsync(counter, shortage.Setup.WarehouseId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // El sistema tiene 8 en la ubicación: ni ese número ni un campo que lo lleve viaja al operario.
        using var document = JsonDocument.Parse(await BodyAsync(response));
        var line = document.RootElement.GetProperty("lines")[0];
        Assert.Equal(0m, line.GetProperty("quantityPlanned").GetDecimal());
        Assert.Equal(0m, line.GetProperty("quantityRemaining").GetDecimal());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("quantityCounted").ValueKind);
        var quantityFields = line.EnumerateObject()
            .Select(property => property.Name)
            .Where(name => name.Contains("quantity", StringComparison.OrdinalIgnoreCase))
            .Order();
        Assert.Equal(KnownQuantityFields, quantityFields);
        Assert.Equal(0m, line.GetProperty("quantityConfirmed").GetDecimal());
        Assert.DoesNotContain(
            document.RootElement.GetProperty("events").EnumerateArray(),
            value => value.GetProperty("quantity").ValueKind != JsonValueKind.Null);
    }
}
