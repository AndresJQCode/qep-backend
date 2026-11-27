using Microsoft.Extensions.DependencyInjection;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Tasks;
using Modules.Wms.Domain.Warehousing;
using static Modules.Wms.IntegrationTests.WmsApiHarness;

namespace Modules.Wms.IntegrationTests.Tasks;

/// <summary>Cómo siembran tareas las pruebas del motor mientras no pasan por la API: el agregado se arma con el dominio y se guarda
/// por los mismos puertos que usa un handler.</summary>
internal static class TaskTestSupport
{
    /// <summary>Un movimiento de una línea. <paramref name="createdAt"/> es también desde cuándo está en el pool si se publica.</summary>
    public static WarehouseTask NewMove(
        LedgerSetup s,
        Guid from,
        Guid to,
        decimal qty = 5,
        bool publish = true,
        Guid? assignTo = null,
        int? priority = null,
        DateTimeOffset? dueAt = null,
        DateTimeOffset? createdAt = null,
        Guid? createdBy = null) =>
        WarehouseTask.CreateMove(
            s.TenantId,
            $"T-{Guid.NewGuid():N}"[..32],
            new WarehouseId(s.WarehouseId),
            new OwnerId(s.OwnerId),
            priority,
            dueAt,
            null,
            [Line(s, from, to, qty)],
            assignTo,
            publish,
            createdBy ?? Guid.CreateVersion7(),
            createdAt ?? DateTimeOffset.UtcNow);

    /// <summary>Un conteo de verificación de una línea, publicado, con una novedad como origen (el id que se pase).</summary>
    public static WarehouseTask NewCount(
        LedgerSetup s, Guid location, Guid? sourceId = null, Guid? lot = null, DateTimeOffset? createdAt = null) =>
        WarehouseTask.CreateCount(
            s.TenantId,
            $"T-{Guid.NewGuid():N}"[..32],
            new WarehouseId(s.WarehouseId),
            new OwnerId(s.OwnerId),
            [WarehouseTaskLine.CreateCount(
                s.VariantId, lot is { } id ? new LotId(id) : null, StockStatus.Available, new LocationId(location))],
            TaskSourceType.Incident,
            sourceId ?? Guid.CreateVersion7(),
            createdAt ?? DateTimeOffset.UtcNow);

    public static WarehouseTaskLine Line(
        LedgerSetup s, Guid from, Guid to, decimal qty = 5, Guid? lot = null, StockStatus status = StockStatus.Available) =>
        WarehouseTaskLine.Create(
            s.VariantId,
            lot is { } id ? new LotId(id) : null,
            status,
            new LocationId(from),
            new LocationId(to),
            qty);

    /// <summary>El segundo bin que toda tarea de movimiento necesita como destino.</summary>
    public static async Task<Guid> CreateDestinationAsync(LedgerSetup s, string code = "B-01-01") =>
        (await CreateBinAsync(s.Manager, s.WarehouseId, code[..1], code)).Id;

    public static async Task SaveAsync(QepApiFactory factory, params WarehouseTask[] tasks)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWarehouseTaskRepository>();
        foreach (var task in tasks)
        {
            repository.Add(task);
        }

        await scope.ServiceProvider.GetRequiredService<IWmsUnitOfWork>().SaveChangesAsync(ct);
    }

    /// <summary>La ficha tal como está en la base.</summary>
    public static async Task<WarehouseTask> ReloadAsync(QepApiFactory factory, WarehouseTask task)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var reloaded = await scope.ServiceProvider.GetRequiredService<IWarehouseTaskRepository>()
            .GetWithEventsAsync(task.TenantId, task.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(reloaded);
        return reloaded;
    }

    /// <summary>La historia en el orden del servidor (<c>RecordedAt</c>); los hechos de una transacción comparten fecha y un UUID v7 no
    /// ordena dentro del mismo milisegundo: dentro de una fecha manda el tipo, que el enum declara en orden causal.</summary>
    public static IReadOnlyList<WarehouseTaskEvent> History(WarehouseTask task) =>
        [.. task.Events.OrderBy(value => value.RecordedAt).ThenBy(value => value.Kind).ThenBy(value => value.Id)];
}
