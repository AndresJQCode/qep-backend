using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Application;
using Modules.Wms.Application;
using Testcontainers.PostgreSql;
using static Modules.Wms.IntegrationTests.AdjustmentTestSupport;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests;

/// <summary>§10.2: los handlers ya llaman a la autorización con el dueño y la bodega que el documento implica, para que activar el
/// scope de la fase 3 no toque ningún caso de uso. Hoy la autorización los ignora, así que sólo un espía lo prueba.</summary>
public sealed class AuthorizationScopeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Call(string Permission, Guid? OwnerId, Guid? WarehouseId);

    private sealed class SpyAuthorization(ITenantModules tenantModules, ConcurrentQueue<Call> calls) : WmsAuthorization(tenantModules)
    {
        public override Task EnsureAsync(
            IExecutionContext ctx, Guid tenantId, string permission, Guid? ownerId = null, Guid? warehouseId = null, CancellationToken ct = default)
        {
            calls.Enqueue(new Call(permission, ownerId, warehouseId));
            return base.EnsureAsync(ctx, tenantId, permission, ownerId, warehouseId, ct);
        }
    }

    private static Task<(PostgreSqlContainer Database, QepApiFactory Factory)> OpenSpiedAsync(ConcurrentQueue<Call> calls) =>
        OpenAsync(configureServices: services =>
            services.AddScoped<WmsAuthorization>(provider => new SpyAuthorization(provider.GetRequiredService<ITenantModules>(), calls)));

    private static bool Scoped(IEnumerable<Call> calls, string permission, LedgerSetup s) =>
        calls.Any(call => call.Permission == permission && call.OwnerId == s.OwnerId && call.WarehouseId == s.WarehouseId);

    [Fact]
    public async Task EveryAdjustmentHandlerPassesTheOwnerAndWarehouseTheDocumentImplies()
    {
        var calls = new ConcurrentQueue<Call>();
        var (database, factory) = await OpenSpiedAsync(calls);
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);

        calls.Clear();
        var proposed = await ProposeAsync(s.Manager, Body(s, [Line(s.BinA, s.VariantId, 5)]));
        Assert.True(Scoped(calls, WmsPermissions.AdjustmentPropose, s), "propose");

        calls.Clear();
        var approve = await ActionRawAsync(s.Manager, proposed.Id, "approve", new { version = proposed.Version });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        Assert.True(Scoped(calls, WmsPermissions.AdjustmentApprove, s), "approve");

        calls.Clear();
        var post = await PostRawAsync(s.Manager, proposed.Id, proposed.Version + 1);
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.True(Scoped(calls, WmsPermissions.AdjustmentApprove, s), "post");

        var other = await ProposeAsync(s.Manager, Body(s, [Line(s.BinA, s.VariantId, 1)]));
        calls.Clear();
        var reject = await ActionRawAsync(s.Manager, other.Id, "reject", new { version = other.Version, note = "no" });
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        Assert.True(Scoped(calls, WmsPermissions.AdjustmentApprove, s), "reject");

        calls.Clear();
        var movementId = await ScalarAsync<Guid>(database, "SELECT id FROM wms.stock_movements WHERE document_id = @id", ("id", proposed.Id));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{WmsUrl()}/stock/movements/{movementId}/reverse")
        {
            Content = JsonContent.Create(new { note = "x" }),
        };
        request.Headers.Add("Idempotency-Key", "7a0d1104-5722-5077-8079-b85c6aa90199");
        var reverse = await s.Manager.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Created, reverse.StatusCode);
        Assert.True(Scoped(calls, WmsPermissions.AdjustmentApprove, s), "reverse");
    }

    [Fact]
    public async Task AnotherTenantsDocumentIsStill403BeforeAnythingIsLoaded()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var approved = await ApprovedAsync(s.Manager, s, s.BinA, 1);
        using var intruder = CreateClient(factory, OtherSubjectId, OtherTenantId, AllPermissions);

        var response = await PostRawAsync(intruder, approved.Id, approved.Version);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
