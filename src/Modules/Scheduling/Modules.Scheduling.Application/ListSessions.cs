using BuildingBlocks.Application;
using FluentValidation;
using Modules.Scheduling.Domain;
using Modules.Tenancy.Application;

namespace Modules.Scheduling.Application;

/// <summary>
/// Los parámetros de <c>GET sessions</c>: el rango <c>from</c>–<c>to</c> (días del tenant, ambos inclusive) y, opcional, un recurso. Todo es
/// anulable para que el validador devuelva el campo y no un 400 de binding.
/// </summary>
public sealed record ListSessionsRequest(DateOnly? From, DateOnly? To, Guid? ResourceId);

public sealed class ListSessionsRequestValidator : SchedulingValidator<ListSessionsRequest>
{
    public ListSessionsRequestValidator()
    {
        RuleFor(request => request.From).NotNull().WithMessage("Indica desde qué día.");
        RuleFor(request => request.To).NotNull().WithMessage("Indica hasta qué día.");
        RuleFor(request => request.To)
            .Must((request, to) => to >= request.From).WithMessage("El rango no puede terminar antes de empezar.")
            .Must((request, to) => to!.Value.DayNumber - request.From!.Value.DayNumber <= CalendarRequestValidator.MaxDays)
            .WithMessage($"Pide como máximo {CalendarRequestValidator.MaxDays} días a la vez.")
            .When(request => request.From is not null && request.To is not null);
        RuleFor(request => request.ResourceId)
            .Must(id => id is null || id.Value != Guid.Empty).WithMessage("Elige un recurso válido.");
    }
}

public sealed record ListSessionsQuery(Guid TenantId, ListSessionsRequest Request) : IQuery<IReadOnlyList<PublishedSessionDto>>;

/// <summary>
/// Las sesiones que el tenant publicó (spec 2026-11-23 §1): activas, de recursos no archivados y cuyo inicio cae en el rango de días del
/// tenant, por inicio y luego por recurso. Quien no ve todas las agendas recibe sólo las de sus recursos; pedir un recurso ajeno por id
/// responde 403, como en el calendario.
/// </summary>
public sealed class ListSessionsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    SchedulingFirstAccess firstAccess,
    ResourceScopeResolver scopeResolver,
    IResourceRepository resources,
    ISessionRepository sessions,
    IValidator<ListSessionsRequest> validator)
    : IQueryHandler<ListSessionsQuery, IReadOnlyList<PublishedSessionDto>>
{
    public async Task<IReadOnlyList<PublishedSessionDto>> HandleAsync(ListSessionsQuery query, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken,
            SchedulingPermissions.AppointmentRead, SchedulingPermissions.AvailabilityManage);
        var request = query.Request;
        await validator.ValidateAndThrowAsync(request, cancellationToken);
        await firstAccess.EnsureAsync(query.TenantId, cancellationToken);

        var scope = await scopeResolver.ResolveAsync(query.TenantId, cancellationToken);
        if (request.ResourceId is { } requested && !scope.Includes(requested))
        {
            throw SchedulingAuthorization.Denied();
        }

        var calendar = await tenantClock.GetAsync(query.TenantId, cancellationToken);
        var rangeStart = calendar.StartOfDayUtc(request.From!.Value);
        var rangeEnd = calendar.EndOfDayExclusiveUtc(request.To!.Value);
        var offered = (await resources.ListAsync(query.TenantId, scope, cancellationToken))
            .Where(resource => !resource.Archived && (request.ResourceId is not { } only || resource.Id.Value == only))
            .Select(resource => resource.Id)
            .ToArray();
        if (offered.Length == 0)
        {
            return [];
        }

        var found = await sessions.ListOverlappingAsync(query.TenantId, offered, rangeStart, rangeEnd, onlyActive: true, cancellationToken);
        return [.. found
            .Where(session => session.IsPublished && session.IsActive && session.StartsAt >= rangeStart && session.StartsAt < rangeEnd)
            .OrderBy(session => session.StartsAt)
            .ThenBy(session => session.ResourceId.Value)
            .Select(SchedulingComposer.PublishedSession)];
    }
}
