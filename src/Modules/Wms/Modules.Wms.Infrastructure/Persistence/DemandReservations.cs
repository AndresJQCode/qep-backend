using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.Infrastructure.Persistence;

/// <summary>
/// Las reservas de demanda sobre <c>wms.reservations</c> (location_id nulo). No escribe saldos: el ledger sigue siendo el único que
/// los toca. Cerrar y consumir guardan antes de volver (<see cref="IWmsUnitOfWork.FlushAsync"/>): el índice único parcial de las
/// abiertas no es diferible, y EF no garantiza que el UPDATE de la vieja vaya antes del INSERT de la nueva.
/// <para><b>Orden de bloqueos.</b> Ninguna operación toca <c>balance_change_queue</c> por su cuenta: sólo anota las claves. Quien
/// llama las encola con <see cref="EnqueueChangesAsync"/> <b>después</b> del ledger, porque la cola es el último bloqueo de una
/// transacción (<c>InventoryLedger</c>, S26). Encolar antes tomaría la fila de la cola y luego la del saldo, al revés que un
/// asiento concurrente: un deadlock 40P01.</para>
/// </summary>
internal sealed class DemandReservations(
    WmsDbContext dbContext,
    IWmsUnitOfWork unitOfWork,
    IBalanceChangeQueue balanceChanges,
    IClock clock) : IDemandReservations
{
    private readonly HashSet<BalanceChangeKey> _pending = [];

    public Task OpenAsync(DemandLine line, Guid warehouseId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(line);

        dbContext.Set<Reservation>().Add(Reservation.OpenDemand(
            line.TenantId,
            line.OwnerId,
            warehouseId,
            line.VariantId,
            line.Quantity,
            line.DemandType,
            line.DemandId,
            line.DemandLineId,
            clock.UtcNow));
        _pending.Add(new BalanceChangeKey(line.TenantId, line.OwnerId, warehouseId, line.VariantId));
        return Task.CompletedTask;
    }

    public async Task EnqueueChangesAsync(CancellationToken ct)
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var keys = _pending.ToArray();
        _pending.Clear();
        await balanceChanges.EnqueueAsync(keys, ct);
    }

    public async Task CloseAllAsync(Guid tenantId, string demandType, Guid demandId, CancellationToken ct)
    {
        var open = await LoadOpenAsync(tenantId, demandType, demandId, ct);
        foreach (var reservation in open)
        {
            reservation.Close();
        }

        await unitOfWork.FlushAsync(ct);
        Track(open);
    }

    public async Task<IReadOnlyList<Reservation>> ConsumeAllAsync(Guid tenantId, string demandType, Guid demandId, CancellationToken ct)
    {
        var open = await LoadOpenAsync(tenantId, demandType, demandId, ct);
        foreach (var reservation in open)
        {
            reservation.Consume();
        }

        await unitOfWork.FlushAsync(ct);
        Track(open);
        return open;
    }

    public Task ReopenAsync(IReadOnlyList<Reservation> consumed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(consumed);

        var now = clock.UtcNow;
        foreach (var reservation in consumed)
        {
            dbContext.Set<Reservation>().Add(Reservation.OpenDemand(
                reservation.TenantId,
                reservation.OwnerId,
                reservation.WarehouseId,
                reservation.VariantId,
                reservation.Quantity,
                reservation.DemandType,
                reservation.DemandId,
                reservation.DemandLineId ?? throw new InvalidOperationException("A demand reservation always carries its line."),
                now));
        }

        Track(consumed);
        return Task.CompletedTask;
    }

    // Sólo las de demanda (sin ubicación): una asignación de T5 sobre el mismo pedido no se cierra ni se consume acá.
    private async Task<List<Reservation>> LoadOpenAsync(Guid tenantId, string demandType, Guid demandId, CancellationToken ct) =>
        await dbContext.Set<Reservation>()
            .Where(reservation => reservation.TenantId == tenantId
                && reservation.DemandType == demandType
                && reservation.DemandId == demandId
                && reservation.Status == ReservationStatus.Open
                && reservation.LocationId == null)
            .ToListAsync(ct);

    private void Track(IEnumerable<Reservation> reservations)
    {
        foreach (var reservation in reservations)
        {
            _pending.Add(new BalanceChangeKey(reservation.TenantId, reservation.OwnerId, reservation.WarehouseId, reservation.VariantId));
        }
    }
}
