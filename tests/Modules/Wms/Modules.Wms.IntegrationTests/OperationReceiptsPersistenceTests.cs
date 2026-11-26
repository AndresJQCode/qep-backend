using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Wms.Application;
using static Modules.Wms.IntegrationTests.WmsApiHarness;

namespace Modules.Wms.IntegrationTests;

/// <summary><c>OperationReceipts.Record</c> y la traducción de la clave primaria del recibo, que el filtro de idempotencia da por
/// buenas y ninguna prueba de ruta ejercía por separado.</summary>
public sealed class OperationReceiptsPersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Stored(string Number, int Lines);

    // El recibo exige un hash SHA-256 en hexadecimal.
    private static readonly string Hash = new('a', 64);

    private static void Activate(AsyncServiceScope scope, string key, string hash)
    {
        var state = scope.ServiceProvider.GetRequiredService<IdempotencyScope>();
        state.TenantId = Guid.Parse(TenantId);
        state.Key = key;
        state.RequestHash = hash;
        state.Source = IdempotencyKeySource.Header;
    }

    [Fact]
    public async Task RecordStoresTheSerializedResponseWithTheScopeKeyAndHashInTheHandlersSave()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        await using var scope = factory.Services.CreateAsyncScope();
        Activate(scope, "K-1", Hash);
        var receipts = scope.ServiceProvider.GetRequiredService<IOperationReceipts>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IWmsUnitOfWork>();

        receipts.Record(new Stored("ADJ-1", 3));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.operation_receipts"));
        await unitOfWork.SaveChangesAsync(Ct);

        var found = await receipts.FindAsync(Guid.Parse(TenantId), "K-1", Ct);
        Assert.NotNull(found);
        Assert.Equal(Hash, found.RequestHash);
        using var json = JsonDocument.Parse(found.ResponseJson);
        Assert.Equal("ADJ-1", json.RootElement.GetProperty("number").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("lines").GetInt32());
        Assert.Null(await receipts.FindAsync(Guid.Parse(TenantId), "K-2", Ct));
        Assert.Null(await receipts.FindAsync(Guid.CreateVersion7(), "K-1", Ct));
    }

    [Fact]
    public async Task RecordWithoutAnActiveScopeThrows()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        await using var scope = factory.Services.CreateAsyncScope();
        var receipts = scope.ServiceProvider.GetRequiredService<IOperationReceipts>();

        Assert.Throws<InvalidOperationException>(() => receipts.Record(new Stored("ADJ-1", 1)));
    }

    [Fact]
    public async Task ASecondReceiptWithTheSameKeyIsTranslatedToOperationReplayed()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        await using var winnerScope = factory.Services.CreateAsyncScope();
        Activate(winnerScope, "K-DUP", Hash);
        winnerScope.ServiceProvider.GetRequiredService<IOperationReceipts>().Record(new Stored("ADJ-1", 1));
        await winnerScope.ServiceProvider.GetRequiredService<IWmsUnitOfWork>().SaveChangesAsync(Ct);

        await using var loserScope = factory.Services.CreateAsyncScope();
        Activate(loserScope, "K-DUP", Hash);
        loserScope.ServiceProvider.GetRequiredService<IOperationReceipts>().Record(new Stored("ADJ-2", 1));

        var replayed = await Assert.ThrowsAsync<OperationReplayedException>(
            () => loserScope.ServiceProvider.GetRequiredService<IWmsUnitOfWork>().SaveChangesAsync(Ct));

        Assert.Equal(Guid.Parse(TenantId), replayed.TenantId);
        Assert.Equal("K-DUP", replayed.Key);
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.operation_receipts"));
    }
}
