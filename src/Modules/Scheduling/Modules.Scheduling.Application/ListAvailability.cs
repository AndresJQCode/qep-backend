using BuildingBlocks.Application;
using FluentValidation;
using Modules.Scheduling.Domain;
using Modules.Tenancy.Application;

namespace Modules.Scheduling.Application;

public sealed record ListAvailabilityQuery(Guid TenantId, Guid? ServiceId, DateOnly? From, DateOnly? To, Guid? ResourceId)
    : IQuery<AvailabilityDto>;

/// <summary>
/// Los espacios libres de un servicio (spec 2026-11-21, «Disponibilidad»): lo calcula <see cref="AvailabilityEngine"/>, nunca se guarda.
/// Sólo ofrece los recursos del alcance de quien pregunta, y un recurso pedido fuera de su alcance responde 403 antes de decir si existe.
/// La anticipación mínima y el horizonte los aplica el motor con las reglas del tenant.
/// </summary>
public sealed class ListAvailabilityHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    SchedulingFirstAccess firstAccess,
    ResourceScopeResolver scopeResolver,
    IServiceRepository services,
    IResourceRepository resources,
    AvailabilitySearch search,
    IValidator<AvailabilityRequest> validator)
    : IQueryHandler<ListAvailabilityQuery, AvailabilityDto>
{
    public async Task<AvailabilityDto> HandleAsync(ListAvailabilityQuery query, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken, SchedulingPermissions.AppointmentRead);
        await validator.ValidateAndThrowAsync(new AvailabilityRequest(query.ServiceId, query.From, query.To, query.ResourceId), cancellationToken);
        await firstAccess.EnsureAsync(query.TenantId, cancellationToken);

        var scope = await scopeResolver.ResolveAsync(query.TenantId, cancellationToken);
        ResourceId? onlyResource = null;
        if (query.ResourceId is { } requested)
        {
            if (!scope.Includes(requested))
            {
                throw SchedulingAuthorization.Denied();
            }

            var resource = await resources.FindAsync(query.TenantId, new ResourceId(requested), cancellationToken);
            if (resource is null || resource.Archived)
            {
                throw SchedulingNotFound.Resource(requested);
            }

            onlyResource = resource.Id;
        }

        var service = await services.FindAsync(query.TenantId, new ServiceId(query.ServiceId!.Value), cancellationToken);
        if (service is null || service.Archived)
        {
            throw SchedulingNotFound.Service(query.ServiceId.Value);
        }

        var found = await search.SearchAsync(query.TenantId, scope, service, query.From!.Value, query.To!.Value, onlyResource, cancellationToken);

        var names = found.ResourceNames;
        return new AvailabilityDto(
            found.TimeZone.Id,
            [.. found.Slots.Select(slot => new AvailabilitySlotDto(
                slot.StartsAt,
                slot.EndsAt,
                slot.SeatsLeft,
                [.. slot.Options.Select(option => new AvailabilityOptionDto(
                    option.ResourceId.Value, names[option.ResourceId], option.SeatsLeft, option.ExistingSessionId?.Value))]))]);
    }
}
