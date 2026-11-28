using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.Infrastructure.Persistence;

/// <summary>
/// Las reservas de demanda sobre <c>wms.reservations</c> (location_id nulo). No escribe saldos: el ledger sigue siendo el único que
/// los toca. Cerrar y consumir guardan antes de volver (<see cref="IWmsUnitOfWork.FlushAsync"/>): el índice único parcial de las
/// abiertas no es diferible, y EF no garantiza que el UPDATE de la vieja vaya antes del INSERT de la nueva.
/// </summary>
internal sealed class DemandReservations(
    WmsDbContext dbContext,
    IWmsUnitOfWork unitOfWork,
    IBalanceChangeQueue balanceChanges,
    IClock clock) : IDemandReservations
{
    public async Task OpenAsync(DemandLine line, Guid warehouseId, CancellationToken ct)
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
        await balanceChanges.EnqueueAsync([new BalanceChangeKey(line.TenantId, line.OwnerId, warehouseId, line.VariantId)], ct);
    }

    public async Task CloseAllAsync(Guid tenantId, string demandType, Guid demandId, CancellationToken ct)
    {
        var open = await LoadOpenAsync(tenantId, demandType, demandId, ct);
        foreach (var reservation in open)
        {
            reservation.Close();
        }

        await unitOfWork.FlushAsync(ct);
        await EnqueueAsync(open, ct);
    }

    public async Task<IReadOnlyList<Reservation>> ConsumeAllAsync(Guid tenantId, string demandType, Guid demandId, CancellationToken ct)
    {
        var open = await LoadOpenAsync(tenantId, demandType, demandId, ct);
        foreach (var reservation in open)
        {
            reservation.Consume();
        }

        await unitOfWork.FlushAsync(ct);
        await EnqueueAsync(open, ct);
        return open;
    }

    public async Task ReopenAsync(IReadOnlyList<Reservation> consumed, CancellationToken ct)
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

        await EnqueueAsync(consumed, ct);
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

    private Task EnqueueAsync(IEnumerable<Reservation> reservations, CancellationToken ct) =>
        balanceChanges.EnqueueAsync(
            reservations
                .Select(reservation => new BalanceChangeKey(reservation.TenantId, reservation.OwnerId, reservation.WarehouseId, reservation.VariantId))
                .ToHashSet(),
            ct);
}
