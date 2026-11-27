using Microsoft.Extensions.DependencyInjection;
using Modules.Wms.Application;
using Modules.Wms.Domain.Tasks;
using static Modules.Wms.IntegrationTests.Tasks.TaskTestSupport;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests.Tasks;

/// <summary>Revisión final de T2, L1: validar la tarea de origen de una novedad no la deja rastreada, así que el bloqueo que le sigue
/// (<c>LockVerificationAsync</c>) recarga una fila fresca y no una copia vieja del contexto.</summary>
public sealed class TaskValidationReadPersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReadingAWarehouseToValidateLeavesNothingTrackedSoALaterLoadSeesTheCurrentRow()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var count = NewCount(s, s.BinA);
        await SaveAsync(factory, count);
        await using var scope = factory.Services.CreateAsyncScope();
        var tasks = scope.ServiceProvider.GetRequiredService<IWarehouseTaskRepository>();

        var warehouse = await tasks.FindWarehouseOfAsync(s.TenantId, count.Id, Ct);
        // Otra transacción cancela el conteo entre la validación y el bloqueo.
        await ExecuteAsync(database, "UPDATE wms.tasks SET status = 'Cancelled' WHERE id = @id", ("id", count.Id));
        var reloaded = await tasks.GetAsync(s.TenantId, count.Id, Ct);

        Assert.Equal(s.WarehouseId, warehouse);
        Assert.Equal(WarehouseTaskStatus.Cancelled, reloaded!.Status);
        Assert.Null(await tasks.FindWarehouseOfAsync(Guid.CreateVersion7(), count.Id, Ct));
    }
}
