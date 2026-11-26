using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.Application;

/// <summary>Un contrato con su cambio programado pendiente y la foto de su revisión en una fecha.</summary>
public sealed record PriceReviewEntry(ServiceContract Service, ServicePriceSchedule? Pending, PriceReviewSnapshot Review);

/// <summary>
/// Las revisiones abiertas de un tenant: los contratos <c>Active</c> en <c>Due</c> o <c>Held</c> (spec reajuste §6.6). Se
/// calculan en memoria con <see cref="PriceReview.Of"/> sobre dos consultas (contratos y pendientes), sin columna materializada:
/// una sola regla para el resumen, la lista y el digest.
/// </summary>
public sealed class PriceReviewReader(IServiceContractRepository services, IServicePriceScheduleRepository schedules)
{
    public async Task<IReadOnlyList<PriceReviewEntry>> ListOpenAsync(
        Guid tenantId, DateOnly today, CancellationToken cancellationToken)
    {
        var active = await services.ListActiveAsync(tenantId, cancellationToken);
        var pending = (await schedules.ListPendingByTenantAsync(tenantId, cancellationToken))
            .ToDictionary(schedule => schedule.ServiceId);

        return active
            .Select(service =>
            {
                var schedule = pending.GetValueOrDefault(service.Id);
                return new PriceReviewEntry(service, schedule, PriceReview.Of(service, schedule, today));
            })
            .Where(entry => entry.Review.State is ReviewState.Due or ReviewState.Held)
            .ToList();
    }

    public static PriceReviewCountsDto Count(IEnumerable<PriceReviewEntry> entries)
    {
        var states = entries.Select(entry => entry.Review.State).ToList();
        return new PriceReviewCountsDto(
            states.Count(state => state == ReviewState.Due), states.Count(state => state == ReviewState.Held));
    }
}
