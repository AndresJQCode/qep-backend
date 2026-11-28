using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.Application;

/// <summary>Una línea de un documento (pedido) que pide stock: lo que el inventario básico reserva sin ubicación.</summary>
public sealed record DemandLine(
    Guid TenantId,
    Guid OwnerId,
    Guid VariantId,
    decimal Quantity,
    string DemandType,
    Guid DemandId,
    Guid DemandLineId);

/// <summary>
/// Las reservas de demanda del inventario básico (spec §5.4): por bodega, variante y dueño, <b>sin ubicación</b>, así que no tocan
/// <c>StockBalance.Reserved</c> ni frenan un movimiento. Corren en la transacción del llamador (<c>BeginTransactionAsync</c>) y cada
/// operación encola el cambio de saldo de (tenant, dueño, bodega, variante), para que la pantalla se entere aunque no haya asiento.
/// Sólo atienden las reservas SIN ubicación: las asignaciones por ubicación (T5) son de otro nivel.
/// </summary>
public interface IDemandReservations
{
    /// <summary>Abre la reserva de una línea. Una sola abierta por línea: abrir otra sin cerrar la anterior choca con
    /// <c>IX_reservations_open_demand</c>.</summary>
    Task OpenAsync(DemandLine line, Guid warehouseId, CancellationToken ct);

    /// <summary>Libera todas las reservas de demanda abiertas del documento y las guarda antes de volver, para que una reserva nueva de
    /// la misma línea no choque con la vieja.</summary>
    Task CloseAllAsync(Guid tenantId, string demandType, Guid demandId, CancellationToken ct);

    /// <summary>Consume todas las reservas de demanda abiertas del documento (la mercancía salió) y devuelve lo consumido: de ahí sale la
    /// reserva nueva si el asiento se deshace (<see cref="ReopenAsync"/>).</summary>
    Task<IReadOnlyList<Reservation>> ConsumeAllAsync(Guid tenantId, string demandType, Guid demandId, CancellationToken ct);

    /// <summary>Vuelve a abrir una reserva por cada una de las consumidas, con su misma bodega, variante, línea y cantidad.</summary>
    Task ReopenAsync(IReadOnlyList<Reservation> consumed, CancellationToken ct);
}
