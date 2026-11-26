using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;
using static Modules.Wms.IntegrationTests.AdjustmentTestSupport;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests;

/// <summary>La idempotencia de los ajustes (RNF-02, S2): <c>POST /adjustments</c> es IK (header) y <c>post</c> es SK (clave del
/// servidor derivada de la versión).</summary>
public sealed class IdempotencyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // El recibo se guarda en una columna jsonb, que normaliza el espaciado y el orden de las propiedades: la repetición devuelve el
    // mismo documento, no los mismos bytes que la primera respuesta.
    private static void AssertSameJson(string expected, string actual) =>
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)),
            $"Expected the same JSON document.{Environment.NewLine}{expected}{Environment.NewLine}{actual}");

    /// <summary>Modela la carrera «la ganadora commiteó después de que la perdedora miró»: la primera búsqueda del recibo hace como si
    /// no existiera, y las siguientes (la del <c>catch</c> del filtro) lo ven. Sin esto el orden de las dos peticiones no se puede
    /// fijar desde fuera.</summary>
    private sealed class ReceiptLookupGate
    {
        private int _blind;

        public void BlindNextLookup() => Interlocked.Exchange(ref _blind, 1);

        public bool TryConsume() => Interlocked.CompareExchange(ref _blind, 0, 1) == 1;
    }

    private sealed class BlindFirstLookupReceipts(IOperationReceipts inner, ReceiptLookupGate gate) : IOperationReceipts
    {
        public async Task<OperationReceipt?> FindAsync(Guid tenantId, string key, CancellationToken cancellationToken) =>
            gate.TryConsume() ? null : await inner.FindAsync(tenantId, key, cancellationToken);

        public void Record<TResponse>(TResponse response) => inner.Record(response);

        public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
            inner.PurgeOlderThanAsync(cutoff, cancellationToken);
    }

    [Fact]
    public async Task WithoutTheHeaderItIs428()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);

        var response = await s.Manager.PostAsJsonAsync($"{WmsUrl()}/adjustments", Body(s, [Line(s.BinA, s.VariantId, 5)]), Ct);

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        Assert.Equal("wms.idempotency_key_required", await CodeOfAsync(response));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.adjustments"));
    }

    [Fact]
    public async Task TheSameKeyAndBodyReplayTheStoredResponseWithoutASecondAdjustment()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var body = Body(s, [Line(s.BinA, s.VariantId, 5)]);

        var first = await ProposeRawAsync(s.Manager, body, "b72d8fb5-23ea-56d6-a974-c4d8757d9bf8");
        var second = await ProposeRawAsync(s.Manager, body, "b72d8fb5-23ea-56d6-a974-c4d8757d9bf8");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        AssertSameJson(await BodyAsync(first), await BodyAsync(second));
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.adjustments"));
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.operation_receipts"));
    }

    [Fact]
    public async Task TheSameKeyWithAnotherBodyIs409()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);

        await ProposeRawAsync(s.Manager, Body(s, [Line(s.BinA, s.VariantId, 5)]), "b72d8fb5-23ea-56d6-a974-c4d8757d9bf8");
        var second = await ProposeRawAsync(s.Manager, Body(s, [Line(s.BinA, s.VariantId, 6)]), "b72d8fb5-23ea-56d6-a974-c4d8757d9bf8");

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("wms.idempotency_key_reused", await CodeOfAsync(second));
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.adjustments"));
    }

    [Fact]
    public async Task AnEquivalentBodyWithAnotherPropertyOrderReplaysInsteadOfConflicting()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var body = Body(s, [Line(s.BinA, s.VariantId, 5)]);
        var first = await ProposeRawAsync(s.Manager, body, "b72d8fb5-23ea-56d6-a974-c4d8757d9bf8");

        // Las mismas propiedades al revés, enviadas como texto: el hash sale del cuerpo ya enlazado, no de los bytes.
        var original = JsonSerializer.SerializeToNode(body, JsonSerializerOptions.Web)!.AsObject();
        var reversed = new JsonObject();
        foreach (var property in original.Reverse())
        {
            reversed[property.Key] = property.Value?.DeepClone();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{WmsUrl()}/adjustments")
        {
            Content = new StringContent(reversed.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", "b72d8fb5-23ea-56d6-a974-c4d8757d9bf8");
        var second = await s.Manager.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.adjustments"));
    }

    [Fact]
    public async Task TwoSimultaneousRequestsWithTheSameKeyLeaveOneAdjustment()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var body = Body(s, [Line(s.BinA, s.VariantId, 5)]);

        var responses = await Task.WhenAll(ProposeRawAsync(s.Manager, body, "575a63f4-5eeb-5b3e-9681-6e7c9a79448d"), ProposeRawAsync(s.Manager, body, "575a63f4-5eeb-5b3e-9681-6e7c9a79448d"));

        // La perdedora esperó el lock de numeración y después chocó con la PK del recibo, que el filtro convirtió en la respuesta
        // guardada de la ganadora.
        Assert.Equal(
            new[] { HttpStatusCode.OK, HttpStatusCode.Created },
            responses.Select(response => response.StatusCode).Order().ToArray());
        AssertSameJson(await BodyAsync(responses[0]), await BodyAsync(responses[1]));
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.adjustments"));
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(DISTINCT number) FROM wms.adjustments"));
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.operation_receipts"));
    }

    [Fact]
    public async Task AnotherTenantReplayingAKeyIs403()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var body = Body(s, [Line(s.BinA, s.VariantId, 5)]);
        await ProposeAsync(s.Manager, body, "abb393db-b777-5c9f-b489-cab0d70a6b2d");
        using var intruder = CreateClient(factory, OtherSubjectId, OtherTenantId, AllPermissions);

        var response = await ProposeRawAsync(intruder, body, "abb393db-b777-5c9f-b489-cab0d70a6b2d");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("authorization.denied", await CodeOfAsync(response));
        Assert.DoesNotContain("ADJ-", await BodyAsync(response), StringComparison.Ordinal);
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.operation_receipts"));
    }

    [Fact]
    public async Task APostReplayedWithTheSameVersionReturnsTheStoredResponseWithoutAHeader()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var approved = await ApprovedAsync(s.Manager, s, s.BinA, 5);

        var first = await PostRawAsync(s.Manager, approved.Id, approved.Version);
        var second = await PostRawAsync(s.Manager, approved.Id, approved.Version);
        var third = await PostRawAsync(s.Manager, approved.Id, approved.Version, headerKey: "609618e5-7701-544a-a83a-9025f18034df");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstJson = await BodyAsync(first);
        Assert.Contains("batchId", firstJson, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        AssertSameJson(firstJson, await BodyAsync(second));
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        AssertSameJson(firstJson, await BodyAsync(third));
        Assert.Equal(
            1L,
            await ScalarAsync<long>(database, "SELECT count(DISTINCT batch_id) FROM wms.stock_movements WHERE document_id = @id", ("id", approved.Id)));
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                database,
                "SELECT count(*) FROM wms.operation_receipts WHERE tenant_id = @t AND idempotency_key = @k",
                ("t", s.TenantId),
                ("k", $"adjustment:{approved.Id}:post:{approved.Version}")));
        Assert.Equal(5m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));
    }

    [Fact]
    public async Task APostWhoseWinnerCommittedAfterTheReceiptLookupReplaysInsteadOfAnswering412()
    {
        var gate = new ReceiptLookupGate();
        var (database, factory) = await OpenAsync(configureServices: services =>
        {
            var original = services.Single(descriptor => descriptor.ServiceType == typeof(IOperationReceipts));
            services.Remove(original);
            services.AddSingleton(gate);
            services.AddScoped<IOperationReceipts>(provider => new BlindFirstLookupReceipts(
                (IOperationReceipts)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!),
                provider.GetRequiredService<ReceiptLookupGate>()));
        });
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var approved = await ApprovedAsync(s.Manager, s, s.BinA, 5);
        var winner = await PostRawAsync(s.Manager, approved.Id, approved.Version);
        Assert.Equal(HttpStatusCode.OK, winner.StatusCode);

        // La perdedora ya había mirado el recibo cuando la ganadora commiteó: entra al handler, ve la versión vieja (412) y el filtro
        // relee el recibo que la ganadora dejó.
        gate.BlindNextLookup();
        var loser = await PostRawAsync(s.Manager, approved.Id, approved.Version);

        Assert.Equal(HttpStatusCode.OK, loser.StatusCode);
        AssertSameJson(await BodyAsync(winner), await BodyAsync(loser));
        Assert.Equal(
            1L,
            await ScalarAsync<long>(database, "SELECT count(DISTINCT batch_id) FROM wms.stock_movements WHERE document_id = @id", ("id", approved.Id)));
        Assert.Equal(5m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));
    }

    [Fact]
    public async Task TwoSimultaneousPostsWithTheSameVersionNeverBookTwice()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var approved = await ApprovedAsync(s.Manager, s, s.BinA, 5);

        var responses = await Task.WhenAll(
            PostRawAsync(s.Manager, approved.Id, approved.Version), PostRawAsync(s.Manager, approved.Id, approved.Version));

        // La perdedora ve el documento tomado (409 «busy», todavía sin recibo que repetir) o ya contabilizado (el recibo, 200): lo que
        // nunca ocurre es un segundo movimiento ni un 412.
        Assert.Contains(HttpStatusCode.OK, responses.Select(response => response.StatusCode));
        Assert.All(responses, response => Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, response.StatusCode.ToString()));
        Assert.Equal(
            1L,
            await ScalarAsync<long>(database, "SELECT count(DISTINCT batch_id) FROM wms.stock_movements WHERE document_id = @id", ("id", approved.Id)));
        Assert.Equal(5m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));
    }
}
