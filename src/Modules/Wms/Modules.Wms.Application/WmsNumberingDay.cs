using Modules.Tenancy.Application;

namespace Modules.Wms.Application;

/// <summary>El día de los números diarios (<c>TSK-</c>, <c>ADJ-</c> e <c>INC-</c>, yyyyMMdd-NNNN): el del huso del tenant, no el de UTC
/// (owner, 2026-11-27, B5). Los números ya emitidos no cambian. Un solo lugar para los diez puntos que numeran, y se corta para el mismo
/// instante <c>now</c> que ya usa el handler, no para uno nuevo.</summary>
public sealed class WmsNumberingDay(ITenantClock tenantClock)
{
    public async Task<DateOnly> OfAsync(Guid tenantId, DateTimeOffset instant, CancellationToken cancellationToken)
    {
        var calendar = await tenantClock.GetAsync(tenantId, cancellationToken);
        return DateOnly.FromDateTime(calendar.ToLocal(instant).DateTime);
    }
}
