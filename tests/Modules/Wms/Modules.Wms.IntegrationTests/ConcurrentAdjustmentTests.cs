using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;
using static Modules.Wms.IntegrationTests.AdjustmentTestSupport;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests;

/// <summary>RNF-03 y S26: dos peticiones que compiten por el mismo saldo o el mismo consecutivo se ordenan, no se pisan.</summary>
public sealed class ConcurrentAdjustmentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TwoPostsTakingEightFromTenEndWithOneSuccessOneInsufficientAndTwoLeft()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await PostedAsync(s.Manager, s, s.BinA, 10);
        var first = await ApprovedAsync(s.Manager, s, s.BinA, -8);
        var second = await ApprovedAsync(s.Manager, s, s.BinA, -8);

        var responses = await Task.WhenAll(
            PostRawAsync(s.Manager, first.Id, first.Version), PostRawAsync(s.Manager, second.Id, second.Version));

        Assert.Equal(
            new[] { HttpStatusCode.OK, HttpStatusCode.UnprocessableEntity },
            responses.Select(response => response.StatusCode).Order().ToArray());
        var failed = responses.Single(response => response.StatusCode == HttpStatusCode.UnprocessableEntity);
        using var problem = JsonDocument.Parse(await BodyAsync(failed));
        Assert.Equal("wms.stock.insufficient", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(2m, problem.RootElement.GetProperty("available").GetDecimal());
        Assert.Equal(2m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));

        // El que falló no quedó a medias: sigue aprobado y sin movimientos.
        var statuses = await ScalarAsync<long>(database, "SELECT count(*) FROM wms.adjustments WHERE status = 'Approved'");
        Assert.Equal(1L, statuses);
    }

    [Fact]
    public async Task TwoSimultaneousProposalsGetConsecutiveNumbers()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var body = Body(s, [Line(s.BinA, s.VariantId, 5)]);

        var responses = await Task.WhenAll(ProposeRawAsync(s.Manager, body), ProposeRawAsync(s.Manager, body));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        var numbers = new List<string>();
        foreach (var response in responses)
        {
            numbers.Add((await response.Content.ReadFromJsonAsync<AdjustmentDto>(Ct))!.Number);
        }

        Assert.Equal(["0001", "0002"], numbers.Select(number => number[^4..]).Order().ToArray());
        Assert.Single(numbers.Select(number => number[..^4]).Distinct());
        Assert.Equal(2L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.adjustments"));
    }
}
