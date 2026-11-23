using Modules.Scheduling.Domain;

namespace Modules.Scheduling.Application;

/// <summary>Los inicios libres de un servicio con los nombres de los recursos que los ofrecen. <paramref name="TimeZone"/> es la del tenant.</summary>
public sealed record AvailabilitySearchResult(
    TimeZoneInfo TimeZone, IReadOnlyList<AvailableSlot> Slots, IReadOnlyDictionary<ResourceId, string> ResourceNames);

/// <summary>
/// El cálculo de disponibilidad sin autorización (spec 2026-11-23 §6.2): lo comparten <c>ListAvailabilityHandler</c> (después de autorizar y
/// acotar al alcance de quien pregunta) y <c>IChannelScheduling</c> (alcance total). Recibe el <see cref="ResourceScope"/> ya resuelto y no
/// mira quién llama.
/// </summary>
public sealed class AvailabilitySearch(AvailabilityLoader loader)
{
    public async Task<AvailabilitySearchResult> SearchAsync(
        Guid tenantId,
        ResourceScope scope,
        Service service,
        DateOnly from,
        DateOnly to,
        ResourceId? onlyResource,
        CancellationToken cancellationToken)
    {
        var loaded = await loader.LoadAsync(tenantId, scope, from, to, cancellationToken);
        var sessions = await loader.SessionsAsync(tenantId, loaded, loaded.OfferedResourceIds, onlyActive: true, cancellationToken);
        var slots = AvailabilityEngine.Compute(loaded.Query(service, sessions, onlyResource));

        return new AvailabilitySearchResult(
            loaded.Calendar.TimeZone, slots, loaded.OfferedResources.ToDictionary(resource => resource.Id, resource => resource.DisplayName));
    }
}
