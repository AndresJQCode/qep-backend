using Microsoft.Extensions.DependencyInjection;
using Modules.Wms.Application;
using static Modules.Wms.IntegrationTests.WmsApiHarness;

namespace Modules.Wms.IntegrationTests;

/// <summary>§11.3: un documento lo procesa una sola petición a la vez, y el lock vive lo que vive la transacción.</summary>
public sealed class WmsUnitOfWorkTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheDocumentLockIsExclusiveWhileTheTransactionIsOpen()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var documentId = Guid.CreateVersion7();
        await using var firstScope = factory.Services.CreateAsyncScope();
        await using var secondScope = factory.Services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<IWmsUnitOfWork>();
        var second = secondScope.ServiceProvider.GetRequiredService<IWmsUnitOfWork>();

        var firstTransaction = await first.BeginTransactionAsync(Ct);
        Assert.True(await first.TryLockDocumentAsync(documentId, Ct));

        await using (await second.BeginTransactionAsync(Ct))
        {
            Assert.False(await second.TryLockDocumentAsync(documentId, Ct));
        }

        // Sin guardar: disponer deshace la transacción y con ella suelta el lock.
        await firstTransaction.DisposeAsync();

        await using (await second.BeginTransactionAsync(Ct))
        {
            Assert.True(await second.TryLockDocumentAsync(documentId, Ct));
        }
    }

    [Fact]
    public async Task TryLockWithoutATransactionThrows()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IWmsUnitOfWork>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.TryLockDocumentAsync(Guid.CreateVersion7(), Ct));
    }
}
