using Modules.Scheduling.Domain;

namespace Modules.Scheduling.Application;

/// <summary>Arma los DTO de la configuración. El nombre del miembro vinculado se resuelve acá, en una sola lectura por lista.</summary>
public sealed class SchedulingComposer(ISchedulingMemberDirectory members)
{
    public static LocationDto Location(Location location) =>
        new(location.Id.Value, location.Name, location.IsMain, location.Archived, location.Version);

    public static ServiceDto Service(Service service) =>
        new(service.Id.Value, service.Name, service.DurationMinutes, service.BufferMinutes, service.BookableByChannel, service.Archived, service.Version);

    public static SettingsDto Settings(SchedulingSettings settings) =>
        new(
            settings.ConfirmationMode.ToString(),
            settings.MinNoticeMinutes,
            settings.HorizonDays,
            settings.ClientCancelCutoffHours,
            settings.ReminderEnabled,
            settings.ReminderOffsetHours,
            settings.ReminderConnectionId,
            settings.ReminderTemplateId,
            settings.Version);

    public async Task<IReadOnlyList<ResourceDto>> ResourcesAsync(
        Guid tenantId, IReadOnlyList<Resource> resources, CancellationToken cancellationToken)
    {
        var memberIds = resources.Where(resource => resource.MemberId is not null).Select(resource => resource.MemberId!.Value).Distinct().ToList();
        var names = memberIds.Count == 0
            ? []
            : (await members.FindAsync(tenantId, memberIds, cancellationToken)).ToDictionary(member => member.MemberId, member => member.DisplayName);

        return [.. resources.Select(resource => new ResourceDto(
            resource.Id.Value,
            resource.LocationId.Value,
            resource.DisplayName,
            resource.MemberId,
            resource.MemberId is { } id && names.TryGetValue(id, out var name) ? name : null,
            resource.Archived,
            resource.Version))];
    }

    public async Task<ResourceDto> ResourceAsync(Guid tenantId, Resource resource, CancellationToken cancellationToken) =>
        (await ResourcesAsync(tenantId, [resource], cancellationToken))[0];
}
