using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Scheduling.Application;

public sealed record GetSetupQuery(Guid TenantId) : IQuery<SetupDto>;

/// <summary>Los dos valores de <see cref="SetupDto.Layout"/>.</summary>
public static class SchedulingLayout
{
    public const string Personal = "Personal";

    public const string Consolidated = "Consolidated";

    /// <summary>Una sede activa y un recurso activo es <c>Personal</c>; cualquier otra cuenta es <c>Consolidated</c> (spec 2026-11-21, D-4).</summary>
    public static string Of(int activeLocations, int activeResources) =>
        activeLocations == 1 && activeResources == 1 ? Personal : Consolidated;
}

/// <summary>El primer acceso a la Agenda: aquí es donde nacen la sede principal y las reglas por defecto.</summary>
public sealed class GetSetupHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    SchedulingFirstAccess firstAccess,
    ISchedulingSetupReader reader)
    : IQueryHandler<GetSetupQuery, SetupDto>
{
    public async Task<SetupDto> HandleAsync(GetSetupQuery query, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken, SchedulingPermissions.AppointmentRead);
        await firstAccess.EnsureAsync(query.TenantId, cancellationToken);

        var snapshot = await reader.ReadAsync(query.TenantId, cancellationToken);
        return new SetupDto(
            snapshot.HasService,
            snapshot.HasResource,
            snapshot.HasBlock,
            SchedulingLayout.Of(snapshot.ActiveLocations, snapshot.ActiveResources));
    }
}
