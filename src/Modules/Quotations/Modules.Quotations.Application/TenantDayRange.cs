using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>Un rango de días del tenant como instantes: <see cref="From"/> inclusivo y
/// <see cref="Before"/> exclusivo. Nulo es "sin ese extremo".</summary>
internal readonly record struct TenantInstantRange(DateTimeOffset? From, DateTimeOffset? Before);

/// <summary>
/// Corta los filtros de fecha del listado y del export en el día del tenant (spec 2026-09-17, punto
/// 3): el desde es su 00:00 local, y el hasta es exclusivo en el 00:00 local del día siguiente, así
/// "hasta el 31" incluye todo el 31 del tenant. El repositorio recibe los instantes y no decide husos.
/// </summary>
internal static class TenantDayRange
{
    /// <summary>Sin fechas no hay nada que cortar, y no se consulta el huso: el listado sin rango no
    /// paga esa ida a la base.</summary>
    public static async Task<TenantInstantRange> ResolveAsync(
        ITenantClock tenantClock,
        Guid tenantId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken)
    {
        if (from is null && to is null)
        {
            return new TenantInstantRange(null, null);
        }

        var calendar = await tenantClock.GetAsync(tenantId, cancellationToken);
        return Of(calendar, from, to);
    }

    public static TenantInstantRange Of(TenantCalendar calendar, DateOnly? from, DateOnly? to) =>
        new(
            from is { } start ? calendar.StartOfDayUtc(start) : (DateTimeOffset?)null,
            to is { } end ? calendar.EndOfDayExclusiveUtc(end) : (DateTimeOffset?)null);
}
