using System.Diagnostics.Metrics;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Wms.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Infrastructure.Persistence;
using Testcontainers.PostgreSql;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests;

/// <summary>El ledger contra el esquema real: doble partida, saldo insuficiente, reservas, orden de bloqueo y traducción por nombre.</summary>
public sealed class InventoryLedgerPostingTests
{
    private const StockStatus Available = StockStatus.Available;
    private const StockStatus Received = StockStatus.Received;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<long> MovementCountAsync(PostgreSqlContainer database) =>
        ScalarAsync<long>(database, "SELECT count(*) FROM wms.stock_movements");

    private static Task<long> BalanceCountAsync(PostgreSqlContainer database) =>
        ScalarAsync<long>(database, "SELECT count(*) FROM wms.stock_balances");

    private static decimal AvailableOf(StockInsufficientException exception) => (decimal)exception.ProblemExtensions["available"]!;

    [Fact]
    public async Task APostedBatchWritesOneMovementAndTwoBalancesWhoseSumIsZero()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var before = DateTimeOffset.UtcNow;

        var posting = await PostAsync(factory, Batch(s, Entry(s, s.Supplier, Available, s.Receiving, Received, 60, MovementReason.Receipt)));

        Assert.Single(posting.MovementIds);
        Assert.Equal(60m, await OnHandAsync(database, s.Receiving, s.VariantId, Received));
        Assert.Equal(-60m, await OnHandAsync(database, s.Supplier, s.VariantId, Available));
        Assert.Equal(
            0m,
            await ScalarAsync<decimal>(database, "SELECT sum(on_hand) FROM wms.stock_balances WHERE tenant_id = @t", ("t", s.TenantId)));
        Assert.Equal(1, await MovementCountAsync(database));
        Assert.Equal(
            1,
            await ScalarAsync<long>(database, "SELECT count(*) FROM wms.stock_movements WHERE recorded_at >= occurred_at AND occurred_at >= @before", ("before", before)));
        Assert.Equal(1, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.balance_change_queue"));
    }

    [Fact]
    public async Task MovingMoreThanTheSourceHoldsFailsWithAvailableAndLeavesNothingWritten()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await StockAsync(factory, s, s.BinA, 10);
        var movements = await MovementCountAsync(database);

        var exception = await Assert.ThrowsAsync<StockInsufficientException>(() =>
            PostAsync(factory, Batch(s, Entry(s, s.BinA, Available, s.Receiving, Available, 12))));

        Assert.Equal(10m, AvailableOf(exception));
        Assert.Equal("A-01-02", exception.ProblemExtensions["locationCode"]);
        Assert.Equal(10m, await OnHandAsync(database, s.BinA, s.VariantId, Available));
        Assert.Equal(movements, await MovementCountAsync(database));
    }

    [Fact]
    public async Task MovingFromAnEmptyKeyFailsWithAvailableZero()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);

        var exception = await Assert.ThrowsAsync<StockInsufficientException>(() =>
            PostAsync(factory, Batch(s, Entry(s, s.BinA, Available, s.Receiving, Available, 1))));

        Assert.Equal(0m, AvailableOf(exception));
        Assert.Equal(0, await BalanceCountAsync(database));
    }

    [Fact]
    public async Task MovingIntoTheReservedPortionFailsWithReservedAndLeavesTheBalance()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await StockAsync(factory, s, s.BinA, 10);
        await ExecuteAsync(
            database,
            "UPDATE wms.stock_balances SET reserved = 4 WHERE location_id = @a AND variant_id = @v",
            ("a", s.BinA),
            ("v", s.VariantId));

        var reserved = await Assert.ThrowsAsync<WmsDomainException>(() =>
            PostAsync(factory, Batch(s, Entry(s, s.BinA, Available, s.Receiving, Available, 8))));

        Assert.Equal("wms.stock.reserved", reserved.Code);
        Assert.Equal(10m, await OnHandAsync(database, s.BinA, s.VariantId, Available));
        Assert.Equal(
            4m,
            await ScalarAsync<decimal>(database, "SELECT reserved FROM wms.stock_balances WHERE location_id = @a", ("a", s.BinA)));

        var insufficient = await Assert.ThrowsAsync<StockInsufficientException>(() =>
            PostAsync(factory, Batch(s, Entry(s, s.BinA, Available, s.Receiving, Available, 12))));

        Assert.Equal(6m, AvailableOf(insufficient));
    }

    [Fact]
    public async Task ASupplierLocationGoesNegativeAndTransitDoesNot()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);

        await PostAsync(factory, Batch(s, Entry(s, s.Supplier, Available, s.Receiving, Available, 5)));

        Assert.Equal(-5m, await OnHandAsync(database, s.Supplier, s.VariantId, Available));
        Assert.True(await ScalarAsync<bool>(database, "SELECT allows_negative FROM wms.stock_balances WHERE location_id = @l", ("l", s.Supplier)));

        await Assert.ThrowsAsync<StockInsufficientException>(() =>
            PostAsync(factory, Batch(s, Entry(s, s.Transit, Available, s.Receiving, Available, 5))));
    }

    // Ni el UPDATE con guarda alcanza a estas filas: su allows_negative no coincide con la ubicación, así que sólo el CHECK las frena.
    [Fact]
    public async Task TheBalanceChecksTranslateByNameAsTheLastResort()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await PostAsync(factory, Batch(s, Entry(s, s.Adjustment, Available, s.Supplier, Available, 3)));
        await ExecuteAsync(
            database, "UPDATE wms.stock_balances SET allows_negative = false WHERE location_id = @l", ("l", s.Supplier));

        var nonNegative = await Assert.ThrowsAsync<WmsDomainException>(() =>
            PostAsync(factory, Batch(s, Entry(s, s.Supplier, Available, s.Receiving, Available, 5))));

        Assert.Equal("wms.stock.insufficient", nonNegative.Code);

        await ExecuteAsync(database, "UPDATE wms.stock_balances SET reserved = 2 WHERE location_id = @l", ("l", s.Supplier));

        var reserved = await Assert.ThrowsAsync<WmsDomainException>(() =>
            PostAsync(factory, Batch(s, Entry(s, s.Supplier, Available, s.Receiving, Available, 2))));

        Assert.Equal("wms.stock.reserved", reserved.Code);
    }

    [Fact]
    public async Task TheProfileRulesApplyBeforeWriting()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await SetTrackingAsync(s.Manager, s.VariantId, "Lot");

        var exception = await Assert.ThrowsAsync<WmsDomainException>(() =>
            PostAsync(factory, Batch(s, Entry(s, s.Supplier, Available, s.Receiving, Available, 5))));

        Assert.Equal("wms.lot.required", exception.Code);
        Assert.Equal(0, await MovementCountAsync(database));
        Assert.Equal(0, await BalanceCountAsync(database));
    }

    [Fact]
    public async Task AnOccurredAtOutsideTheWindowIsRejectedBeforeWriting()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);

        var exception = await Assert.ThrowsAsync<WmsDomainException>(() => PostAsync(
            factory,
            Batch(s, Entry(s, s.Supplier, Available, s.Receiving, Available, 5, occurredAt: DateTimeOffset.UtcNow.AddDays(-31)))));

        Assert.Equal("wms.movement.occurred_at_out_of_range", exception.Code);
        Assert.Equal(0, await MovementCountAsync(database));
        Assert.Equal(0, await BalanceCountAsync(database));
    }

    [Fact]
    public async Task ALotOfAnotherVariantIsNotFound()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await SetTrackingAsync(s.Manager, s.VariantId, "Lot");
        var other = await CreateVariantAsync(factory, "V2", null, [12]);
        var lot = await CreateLotAsync(factory, s, other, "L1");

        var exception = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            PostAsync(factory, Batch(s, Entry(s, s.Supplier, Available, s.Receiving, Available, 5, lot: lot))));

        Assert.Equal("wms.lot.not_found", exception.Code);
        Assert.Equal(0, await BalanceCountAsync(database));
    }

    // La carga inicial crea los lotes y postea en la misma transacción: la FK del saldo exige que el lote ya esté en la base.
    [Fact]
    public async Task ALotAddedInTheSameTransactionCanBePosted()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await SetTrackingAsync(s.Manager, s.VariantId, "Lot");

        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IWmsUnitOfWork>();
        await using (await unitOfWork.BeginTransactionAsync(Ct))
        {
            var lot = Lot.Create(s.TenantId, s.OwnerId, s.VariantId, "L1", null, null, false, DateTimeOffset.UtcNow);
            scope.ServiceProvider.GetRequiredService<ILotRepository>().Add(lot);
            await scope.ServiceProvider.GetRequiredService<IInventoryLedger>().PostAsync(
                Batch(s, Entry(s, s.Supplier, Available, s.Receiving, Available, 5, lot: lot.Id.Value)), Ct);
            await unitOfWork.SaveChangesAsync(Ct);

            Assert.Equal(5m, await OnHandAsync(database, s.Receiving, s.VariantId, Available, lot.Id.Value));
        }
    }

    [Fact]
    public async Task ASerialUnitFollowsItsMovement()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await SetTrackingAsync(s.Manager, s.VariantId, "Serial");
        var serial = Guid.CreateVersion7();
        await ExecuteAsync(
            database,
            """
            INSERT INTO wms.serial_units (id, tenant_id, owner_id, variant_id, serial, current_location_id, status)
            VALUES (@id, @tenant, @owner, @variant, 'S-1', @location, 'Available')
            """,
            ("id", serial),
            ("tenant", s.TenantId),
            ("owner", s.OwnerId),
            ("variant", s.VariantId),
            ("location", s.Supplier));

        await PostAsync(factory, Batch(s, Entry(s, s.Supplier, Available, s.Receiving, Received, 1, serial: serial)));

        Assert.Equal(
            s.Receiving,
            await ScalarAsync<Guid>(database, "SELECT current_location_id FROM wms.serial_units WHERE id = @id", ("id", serial)));
        Assert.Equal(
            "Received",
            await ScalarAsync<string>(database, "SELECT status FROM wms.serial_units WHERE id = @id", ("id", serial)));
    }

    [Fact]
    public async Task TwoBatchesOverTheSameMixedLotKeysInOppositeOrderNeverDeadlock()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await SetTrackingAsync(s.Manager, s.VariantId, "Lot");
        var l1 = await CreateLotAsync(factory, s, s.VariantId, "L1");
        var l2 = await CreateLotAsync(factory, s, s.VariantId, "L2");
        var v2 = await CreateVariantAsync(factory, "V2", null, [12]);
        await StockAsync(factory, s, s.BinA, 100, lot: l1);
        await StockAsync(factory, s, s.BinA, 100, lot: l2);
        await StockAsync(factory, s, s.Receiving, 100, lot: l1);
        await StockAsync(factory, s, s.Receiving, 100, lot: l2);
        await StockAsync(factory, s, s.BinA, 100, variant: v2);
        await StockAsync(factory, s, s.Receiving, 100, variant: v2);

        LedgerBatch First() => Batch(
            s,
            Entry(s, s.BinA, Available, s.Receiving, Available, 1, lot: l1),
            Entry(s, s.Receiving, Available, s.BinA, Available, 1, lot: l2),
            Entry(s, s.BinA, Available, s.Receiving, Available, 1, variant: v2));
        LedgerBatch Second() => Batch(
            s,
            Entry(s, s.Receiving, Available, s.BinA, Available, 1, variant: v2),
            Entry(s, s.BinA, Available, s.Receiving, Available, 1, lot: l2),
            Entry(s, s.Receiving, Available, s.BinA, Available, 1, lot: l1));

        // Un 40P01 sube como excepción y Task.WhenAll la deja caer acá.
        await Task.WhenAll(Enumerable.Range(0, 20).SelectMany(_ => new[]
        {
            Task.Run(() => PostAsync(factory, First()), Ct),
            Task.Run(() => PostAsync(factory, Second()), Ct),
        }));

        Assert.Equal(100m, await OnHandAsync(database, s.BinA, s.VariantId, Available, l1));
        Assert.Equal(100m, await OnHandAsync(database, s.BinA, s.VariantId, Available, l2));
        Assert.Equal(100m, await OnHandAsync(database, s.Receiving, s.VariantId, Available, l1));
        Assert.Equal(100m, await OnHandAsync(database, s.Receiving, s.VariantId, Available, l2));
        Assert.Equal(100m, await OnHandAsync(database, s.BinA, v2, Available));
        Assert.Equal(100m, await OnHandAsync(database, s.Receiving, v2, Available));
        Assert.Equal(2, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.balance_change_queue"));
    }

    // Dos lotes sobre las mismas claves de saldo nunca llegan juntos a la cola: el primer saldo los serializa, y por eso la prueba de
    // arriba no detecta el orden de la cola. Aquí no comparten ningún saldo, y dos sesiones retienen las filas para que cada lote
    // empiece a esperar por la fila que le toca primero. Al soltarlas, en orden de aparición cada uno tendría la que el otro necesita.
    [Fact]
    public async Task TwoBatchesThatShareOnlyTheirQueueKeysNeverDeadlock()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var v2 = await CreateVariantAsync(factory, "V2", null, [12]);
        var binB = (await CreateBinAsync(s.Manager, s.WarehouseId, "A", "B-01")).Id;
        foreach (var (location, variant) in new[] { (s.BinA, s.VariantId), (s.BinA, v2), (binB, s.VariantId), (binB, v2) })
        {
            await StockAsync(factory, s, location, 10, variant: variant);
        }

        var firstHolder = await HoldQueueKeyAsync(factory, s, s.VariantId);
        var secondHolder = await HoldQueueKeyAsync(factory, s, v2);

        var first = Task.Run(
            () => PostAsync(
                factory,
                Batch(
                    s,
                    Entry(s, s.BinA, Available, s.Receiving, Available, 1),
                    Entry(s, s.BinA, Available, s.Receiving, Available, 1, variant: v2))),
            Ct);
        var second = Task.Run(
            () => PostAsync(
                factory,
                Batch(
                    s,
                    Entry(s, binB, Available, s.Adjustment, Available, 1, variant: v2),
                    Entry(s, binB, Available, s.Adjustment, Available, 1))),
            Ct);
        await WaitForBlockedSessionsAsync(database, 2);

        await firstHolder.DisposeAsync();
        await secondHolder.DisposeAsync();

        await Task.WhenAll(first, second);
        Assert.Equal(2, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.balance_change_queue"));
    }

    private static async Task<AsyncServiceScope> HoldQueueKeyAsync(QepApiFactory factory, LedgerSetup s, Guid variantId)
    {
        var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWmsUnitOfWork>().BeginTransactionAsync(Ct);
        await scope.ServiceProvider.GetRequiredService<IBalanceChangeQueue>().EnqueueAsync(
            [new BalanceChangeKey(s.TenantId, s.OwnerId, s.WarehouseId, variantId)], Ct);
        return scope;
    }

    // Sin DO UPDATE el escritor no bloquea la fila ya encolada: el publicador la saca, lee el saldo sin este cambio y el evento se pierde.
    [Fact]
    public async Task APublisherDequeuingWhileAWriterIsUncommittedLeavesTheKeyQueuedAfterTheCommit()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await StockAsync(factory, s, s.BinA, 10);
        await using var writer = factory.Services.CreateAsyncScope();
        await using var publisher = factory.Services.CreateAsyncScope();
        var unitOfWork = writer.ServiceProvider.GetRequiredService<IWmsUnitOfWork>();
        var queue = publisher.ServiceProvider.GetRequiredService<IBalanceChangeQueue>();

        await using (await unitOfWork.BeginTransactionAsync(Ct))
        {
            await writer.ServiceProvider.GetRequiredService<IInventoryLedger>().PostAsync(
                Batch(s, Entry(s, s.BinA, Available, s.Receiving, Available, 4)), Ct);

            await queue.DequeueAllAsync(10, Ct);
            await unitOfWork.SaveChangesAsync(Ct);
        }

        Assert.Equal([new BalanceChangeKey(s.TenantId, s.OwnerId, s.WarehouseId, s.VariantId)], await queue.DequeueAllAsync(10, Ct));
    }

    [Fact]
    public async Task AWriterWaitingOnThePublishersDeleteQueuesTheKeyAgain()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await StockAsync(factory, s, s.BinA, 10);
        await using var publisher = factory.Services.CreateAsyncScope();
        var unitOfWork = publisher.ServiceProvider.GetRequiredService<IWmsUnitOfWork>();
        var queue = publisher.ServiceProvider.GetRequiredService<IBalanceChangeQueue>();
        Task writer;

        await using (await unitOfWork.BeginTransactionAsync(Ct))
        {
            Assert.Single(await queue.DequeueAllAsync(10, Ct));
            writer = Task.Run(() => PostAsync(factory, Batch(s, Entry(s, s.BinA, Available, s.Receiving, Available, 4))), Ct);
            await WaitForBlockedSessionsAsync(database, 1);
            await unitOfWork.SaveChangesAsync(Ct);
        }

        await writer;
        Assert.Equal([new BalanceChangeKey(s.TenantId, s.OwnerId, s.WarehouseId, s.VariantId)], await queue.DequeueAllAsync(10, Ct));
    }

    private static async Task WaitForBlockedSessionsAsync(PostgreSqlContainer database, int sessions)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (await ScalarAsync<long>(database, "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock'") >= sessions)
            {
                return;
            }

            await Task.Delay(25, Ct);
        }

        Assert.Fail("The sessions never blocked on the queue rows.");
    }

    [Fact]
    public async Task AProfileCollisionInsideAnExplicitTransactionStillTranslatesTo412()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);

        await AssertCollisionAsync<RequestConcurrencyException>(
            factory, s, variant => InsertCompetingProfileAsync(database, variant, "V1"), s.VariantId);

        // Sólo la clave del perfil: el identificador de esta petición no choca con nada, así que llega a la otra rama.
        var second = await CreateVariantAsync(factory, "V2", null, [12]);
        await AssertCollisionAsync<RequestConcurrencyException>(
            factory, s, variant => InsertCompetingProfileAsync(database, variant, "OTRO-CODIGO"), second);
    }

    [Fact]
    public async Task AnIdentifierTakenByAnotherVariantInsideAnExplicitTransactionIsTheUsersConflict()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);

        var exception = await AssertCollisionAsync<WmsDomainException>(
            factory,
            s,
            _ => InsertCompetingProfileAsync(database, Guid.CreateVersion7(), "V1"),
            s.VariantId);

        Assert.Equal("wms.identifier.value_taken", exception.Code);
    }

    // Sin los savepoints automáticos de EF: si la traducción dependiera de ellos, la consulta del catch fallaría con 25P02.
    private static async Task<TException> AssertCollisionAsync<TException>(
        QepApiFactory factory, LedgerSetup s, Func<Guid, Task> collide, Guid variantId)
        where TException : Exception
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IWmsUnitOfWork>();
        var dbContext = scope.ServiceProvider.GetRequiredService<WmsDbContext>();
        dbContext.Database.AutoSavepointsEnabled = false;
        TException exception;

        await using (await unitOfWork.BeginTransactionAsync(Ct))
        {
            await scope.ServiceProvider.GetRequiredService<IVariantProfileRepository>().GetOrCreateDefaultAsync(s.TenantId, variantId, Ct);
            await collide(variantId);

            exception = await Assert.ThrowsAsync<TException>(() => unitOfWork.SaveChangesAsync(Ct));

            // La transacción sigue viva para el rollback del llamador.
            Assert.NotNull(dbContext.Database.CurrentTransaction);
        }

        Assert.Null(dbContext.Database.CurrentTransaction);
        return exception;
    }

    // La carrera real: dos peticiones crean el perfil a la vez. La perdedora no se entera, ya encuentra el perfil de la otra.
    [Fact]
    public async Task LosingTheRaceToCreateTheProfileDoesNotFailTheMovement()
    {
        var race = new RacingCatalogReader();
        var (database, factory) = await OpenAsync(configureServices: services =>
            services.AddScoped<IWmsCatalogReader>(_ => race));
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        race.BeforeReading = variant => InsertCompetingProfileAsync(database, variant, "RACE");

        await PostAsync(factory, Batch(s, Entry(s, s.Supplier, Available, s.Receiving, Available, 5)));

        Assert.Equal(5m, await OnHandAsync(database, s.Receiving, s.VariantId, Available));
        Assert.Equal(
            1,
            await ScalarAsync<long>(database, "SELECT count(*) FROM wms.variant_profiles WHERE variant_id = @v", ("v", s.VariantId)));
    }

    private sealed class RacingCatalogReader : IWmsCatalogReader
    {
        public Func<Guid, Task>? BeforeReading { get; set; }

        public async Task<IReadOnlyList<WmsVariantSnapshot>> GetVariantsAsync(
            Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken ct)
        {
            var variantId = variantIds.Single();
            if (BeforeReading is { } race)
            {
                await race(variantId);
            }

            return [new WmsVariantSnapshot(variantId, Guid.CreateVersion7(), "Producto", "Variante", "RACE", null, [], true)];
        }

        public Task<IReadOnlyList<WmsVariantSnapshot>> FindVariantsByCodesAsync(
            Guid tenantId, IReadOnlyCollection<string> codes, CancellationToken ct) =>
            throw new NotSupportedException("This double only serves GetVariantsAsync.");
    }

    [Fact]
    public async Task TwoLotsWithTheSameCodeTranslateTo412()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await CreateLotAsync(factory, s, s.VariantId, "L1");

        await Assert.ThrowsAsync<RequestConcurrencyException>(() => CreateLotAsync(factory, s, s.VariantId, "L1"));
    }

    [Fact]
    public async Task MutatingAMovementIsTranslatedToLedgerImmutable()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await StockAsync(factory, s, s.BinA, 10);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WmsDbContext>();
        var movement = await dbContext.Set<StockMovement>().FirstAsync(Ct);
        dbContext.Entry(movement).Property(value => value.Quantity).CurrentValue = 99m;

        var exception = await Assert.ThrowsAsync<WmsDomainException>(() =>
            scope.ServiceProvider.GetRequiredService<IWmsUnitOfWork>().SaveChangesAsync(Ct));

        Assert.Equal("wms.ledger.immutable", exception.Code);
    }

    [Fact]
    public async Task TheQueueHandsOutEachKeyOnceAndAnEmptyEnqueueDoesNothing()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await StockAsync(factory, s, s.BinA, 10);

        await using var scope = factory.Services.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<IBalanceChangeQueue>();
        await queue.EnqueueAsync([], Ct);

        var keys = await queue.DequeueAllAsync(10, Ct);

        Assert.Equal([new BalanceChangeKey(s.TenantId, s.OwnerId, s.WarehouseId, s.VariantId)], keys);
        Assert.Empty(await queue.DequeueAllAsync(10, Ct));
    }

    [Fact]
    public async Task PostingWithoutATransactionThrows()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);

        await using var scope = factory.Services.CreateAsyncScope();
        var ledger = scope.ServiceProvider.GetRequiredService<IInventoryLedger>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ledger.PostAsync(Batch(s, Entry(s, s.Supplier, Available, s.Receiving, Available, 5)), Ct));
    }

    [Fact]
    public async Task TheMovementsCounterIsIncrementedPerReason()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var meterFactory = factory.Services.GetRequiredService<IMeterFactory>();
        long total = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Name == "qep.wms.movements" && ReferenceEquals(instrument.Meter.Scope, meterFactory))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "reason" && (string?)tag.Value == nameof(MovementReason.Adjustment))
                {
                    Interlocked.Add(ref total, value);
                }
            }
        });
        listener.Start();

        await PostAsync(
            factory,
            Batch(
                s,
                Entry(s, s.Supplier, Available, s.BinA, Available, 1),
                Entry(s, s.Supplier, Available, s.Receiving, Available, 1)));

        Assert.Equal(2, Interlocked.Read(ref total));
    }

    // D-12: la misma (dueño, ubicación, variante, lote, estado) suelta y dentro de una unidad son dos saldos, y lo que entra suelto no
    // toca el de la unidad.
    [Fact]
    public async Task TheSameKeyLooseAndInsideAUnitAreTwoBalances()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var loose = new StockKey(s.OwnerId, s.BinA, s.VariantId, null, Available);
        var inUnit = loose with { HandlingUnitId = Guid.CreateVersion7() };
        await SeedBalanceAsync(database, s, loose, 5);
        await SeedBalanceAsync(database, s, inUnit, 4);

        await AdjustmentTestSupport.PostedAsync(s.Manager, s, s.BinA, 3);

        Assert.Equal(2L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.stock_balances WHERE location_id = @a", ("a", s.BinA)));
        Assert.Equal(8m, await OnHandAsync(database, loose));
        Assert.Equal(4m, await OnHandAsync(database, inUnit));
    }

    // Guard interno mientras no exista el agregado HandlingUnit: ningún endpoint de WMS-D puede producirlo, así que no es un código
    // de negocio sino un error de programación.
    [Fact]
    public async Task APostedBatchWithAHandlingUnitIsRejectedUntilTheAggregateExists()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var entry = new LedgerEntry(
            new StockKey(s.OwnerId, s.Supplier, s.VariantId, null, Available),
            new StockKey(s.OwnerId, s.BinA, s.VariantId, null, Available, Guid.CreateVersion7()),
            5,
            null,
            MovementReason.Adjustment,
            null,
            DateTimeOffset.UtcNow);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => PostAsync(factory, Batch(s, entry)));

        Assert.Equal("Handling units are not supported until the HandlingUnit aggregate exists.", exception.Message);
        Assert.Equal(0, await MovementCountAsync(database));
        Assert.Equal(0, await BalanceCountAsync(database));
    }
}
