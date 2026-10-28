using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;
using static Modules.Pipeline.IntegrationTests.PipelineApiHarness;

namespace Modules.Pipeline.IntegrationTests;

/// <summary>Spec 2026-10-13 §4, D4 y P4 del plan contra Postgres: el embudo se crea una sola vez aunque lleguen dos pedidos a la
/// vez; el historial se guarda con el negocio; la versión vieja es concurrency.conflict; nada cruza tenants.</summary>
public sealed class PipelinePersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Deal> SaveDealAsync(QepApiFactory factory, Guid tenantId, Guid customerId)
    {
        using var scope = factory.Services.CreateScope();
        var pipeline = await scope.ServiceProvider.GetRequiredService<IPipelineProvisioner>().EnsureAsync(tenantId, Ct);
        var deal = Deal.Create(tenantId, pipeline, customerId, Guid.CreateVersion7(), "Dotación", 1_000m, "COP", null,
            DealSource.Manual, null, DealActor.ForSystem, DateTimeOffset.UtcNow);
        scope.ServiceProvider.GetRequiredService<IDealRepository>().Add(deal);
        await scope.ServiceProvider.GetRequiredService<IPipelineUnitOfWork>().SaveChangesAsync(Ct);
        return deal;
    }

    [Fact]
    public async Task TheTenantIndexIsUniqueAndHasTheNameTheProvisionerReliesOn()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        _ = factory.Services;

        var definition = await ScalarAsync<string>(connectionString,
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'pipeline' AND indexname = 'IX_pipelines_tenant'");

        Assert.Contains("UNIQUE", definition, StringComparison.Ordinal);
        Assert.Contains("(tenant_id)", definition, StringComparison.Ordinal);
    }

    // D4: dos peticiones a la vez en el primer acceso no dejan dos embudos ni semillas duplicadas.
    [Fact]
    public async Task ProvisioningSeedsOnceEvenWithTwoConcurrentCalls()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();

        async Task<Guid> EnsureAsync()
        {
            using var scope = factory.Services.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<IPipelineProvisioner>().EnsureAsync(tenantId, Ct)).Id;
        }

        var ids = await Task.WhenAll(EnsureAsync(), EnsureAsync(), EnsureAsync());

        Assert.Single(ids.Distinct());
        Assert.Equal(1L, await CountAsync(connectionString, "SELECT count(*) FROM pipeline.pipelines WHERE tenant_id = @t", ("t", tenantId)));
        Assert.Equal(6L, await CountAsync(connectionString, "SELECT count(*) FROM pipeline.stages WHERE pipeline_id = @p", ("p", ids[0])));
        Assert.Equal(5L, await CountAsync(connectionString, "SELECT count(*) FROM pipeline.reasons WHERE tenant_id = @t AND kind = 'Loss'", ("t", tenantId)));
        Assert.Equal(5L, await CountAsync(connectionString, "SELECT count(*) FROM pipeline.reasons WHERE tenant_id = @t AND kind = 'Dismiss'", ("t", tenantId)));
    }

    // P4: la fila de historial viaja en el mismo SaveChanges que el negocio, y la última abierta sale de la fila de cierre. Mover
    // y perder van en un solo SaveChanges: EF no los inserta en orden de Add, así que el orden tiene que salir del ordinal.
    [Fact]
    public async Task EveryStageChangeIsSavedWithItsHistoryRow()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var saved = await SaveDealAsync(factory, tenantId, Guid.CreateVersion7());

        Guid negotiation;
        using (var scope = factory.Services.CreateScope())
        {
            var pipeline = await scope.ServiceProvider.GetRequiredService<IPipelineProvisioner>().EnsureAsync(tenantId, Ct);
            negotiation = pipeline.OrderedStages.Single(stage => stage.Name == "Negociación").Id;
            var deal = await scope.ServiceProvider.GetRequiredService<IDealRepository>().FindAsync(tenantId, saved.Id, Ct);
            Assert.NotNull(deal);
            deal.MoveTo(pipeline, negotiation, DealActor.ForSystem, DateTimeOffset.UtcNow);
            deal.Lose(pipeline, Guid.CreateVersion7(), null, DealActor.ForSystem, DateTimeOffset.UtcNow);
            await scope.ServiceProvider.GetRequiredService<IPipelineUnitOfWork>().SaveChangesAsync(Ct);
        }

        using var reader = factory.Services.CreateScope();
        var deals = reader.ServiceProvider.GetRequiredService<IDealRepository>();
        var history = await deals.ListHistoryAsync(tenantId, saved.Id, Ct);
        Assert.Equal([1, 2, 3], history.Select(entry => entry.Ordinal));
        Assert.Null(history[0].FromStageId);
        Assert.Equal(negotiation, history[1].ToStageId);
        Assert.Equal(negotiation, await deals.FindLastOpenStageIdAsync(tenantId, saved.Id, Ct));
        Assert.Equal(3L, await ScalarAsync<long>(connectionString, "SELECT version FROM pipeline.deals WHERE id = @id", ("id", saved.Id)));
        Assert.Equal(3, await ScalarAsync<int>(connectionString, "SELECT history_count FROM pipeline.deals WHERE id = @id", ("id", saved.Id)));
        // Spec §4.3: el índice por negocio y tiempo existe con esas columnas.
        Assert.Contains("(tenant_id, deal_id, occurred_at)", await ScalarAsync<string>(connectionString,
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'pipeline' AND indexname = 'IX_deal_stage_history_deal'"), StringComparison.Ordinal);
    }

    // Review Focus 3: otro proceso (el worker) cambió el negocio entre la lectura y el commit.
    [Fact]
    public async Task AStaleVersionIsAConcurrencyConflictAndWritesNoHistory()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var saved = await SaveDealAsync(factory, tenantId, Guid.CreateVersion7());

        using var scope = factory.Services.CreateScope();
        var pipeline = await scope.ServiceProvider.GetRequiredService<IPipelineProvisioner>().EnsureAsync(tenantId, Ct);
        var deal = await scope.ServiceProvider.GetRequiredService<IDealRepository>().FindAsync(tenantId, saved.Id, Ct);
        Assert.NotNull(deal);
        await ExecuteAsync(connectionString, "UPDATE pipeline.deals SET version = version + 1 WHERE id = @id", ("id", saved.Id));
        deal.Win(pipeline, DealActor.ForSystem, DateTimeOffset.UtcNow);

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            scope.ServiceProvider.GetRequiredService<IPipelineUnitOfWork>().SaveChangesAsync(Ct));

        Assert.Equal("concurrency.conflict", error.Code);
        Assert.Equal(1L, await CountAsync(connectionString, "SELECT count(*) FROM pipeline.deal_stage_history WHERE deal_id = @id", ("id", saved.Id)));
    }

    // Spec D38: dos asesores deciden la misma conversación contra el mismo entrante. El segundo SaveChanges choca con el índice
    // único, sale pipeline.triage.already_decided y su negocio no queda; un entrante nuevo es otro instante y se puede decidir.
    [Fact]
    public async Task ASecondDecisionAgainstTheSameInboundIsAlreadyDecidedAndLeavesOneDeal()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var customerId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        // Con microsegundos, como los guarda Postgres: el instante tiene que volver idéntico de la base.
        var inbound = new DateTimeOffset(2026, 10, 13, 15, 0, 0, TimeSpan.Zero).AddTicks(1_234_560);

        async Task DecideAsync(DateTimeOffset lastInboundAt)
        {
            using var scope = factory.Services.CreateScope();
            var pipeline = await scope.ServiceProvider.GetRequiredService<IPipelineProvisioner>().EnsureAsync(tenantId, Ct);
            var deal = Deal.Create(tenantId, pipeline, customerId, Guid.CreateVersion7(), "Laura", null, "COP", null,
                DealSource.Conversation, conversationId, DealActor.ForSystem, DateTimeOffset.UtcNow);
            scope.ServiceProvider.GetRequiredService<IDealRepository>().Add(deal);
            scope.ServiceProvider.GetRequiredService<ITriageDecisionRepository>().Add(
                TriageDecision.Created(tenantId, conversationId, customerId, deal.Id, lastInboundAt, DealActor.ForSystem, DateTimeOffset.UtcNow));
            await scope.ServiceProvider.GetRequiredService<IPipelineUnitOfWork>().SaveChangesAsync(Ct);
        }

        await DecideAsync(inbound);
        var error = await Assert.ThrowsAsync<PipelineDomainException>(() => DecideAsync(inbound));
        var dealsAfterRace = await CountAsync(connectionString, "SELECT count(*) FROM pipeline.deals WHERE tenant_id = @t", ("t", tenantId));
        await DecideAsync(inbound.AddMinutes(10));

        Assert.Equal(PipelineErrorCodes.TriageAlreadyDecided, error.Code);
        Assert.Equal(1L, dealsAfterRace);
        Assert.Equal(2L, await CountAsync(connectionString, "SELECT count(*) FROM pipeline.triage_decisions WHERE tenant_id = @t", ("t", tenantId)));
        Assert.Equal(2L, await CountAsync(connectionString, "SELECT count(*) FROM pipeline.deals WHERE tenant_id = @t", ("t", tenantId)));
    }

    [Fact]
    public async Task RepositoriesNeverCrossTenants()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantA = Guid.CreateVersion7();
        var tenantB = Guid.CreateVersion7();
        var customerId = Guid.CreateVersion7();
        var saved = await SaveDealAsync(factory, tenantA, customerId);

        using var scope = factory.Services.CreateScope();
        var deals = scope.ServiceProvider.GetRequiredService<IDealRepository>();

        Assert.Null(await deals.FindAsync(tenantB, saved.Id, Ct));
        Assert.Empty(await deals.ListOpenByCustomerAsync(tenantB, customerId, Ct));
        Assert.Empty(await deals.CustomersWithOpenDealsAsync(tenantB, [customerId], Ct));
        Assert.Equal([customerId], await deals.CustomersWithOpenDealsAsync(tenantA, [customerId, Guid.CreateVersion7()], Ct));
        Assert.Equal([saved.StageId], await deals.StagesWithDealsAsync(tenantA, saved.PipelineId, Ct));
        Assert.Empty(await deals.ListHistoryAsync(tenantB, saved.Id, Ct));
    }
}
