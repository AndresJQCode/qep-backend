using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Audit.Domain;
using Modules.Geography.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Geography.Application;

/// <summary>Descartar un texto de la bandeja (D-C9): sigue sumando ocurrencias, pero ya no aparece entre los pendientes.</summary>
public sealed record DiscardUnresolvedCityCommand(TenantId TenantId, UnresolvedCityId Id) : ICommand<UnresolvedCityListItemDto>;

/// <summary>Devolver a la bandeja un texto descartado.</summary>
public sealed record RestoreUnresolvedCityCommand(TenantId TenantId, UnresolvedCityId Id) : ICommand<UnresolvedCityListItemDto>;

public sealed class DiscardUnresolvedCityHandler(UnresolvedCityTriage triage)
    : ICommandHandler<DiscardUnresolvedCityCommand, UnresolvedCityListItemDto>
{
    public Task<UnresolvedCityListItemDto> HandleAsync(DiscardUnresolvedCityCommand command, CancellationToken cancellationToken) =>
        triage.ApplyAsync(
            command.TenantId, command.Id, GeographyAuditActions.UnresolvedCityDiscarded, (row, now) => row.Discard(now),
            cancellationToken);
}

public sealed class RestoreUnresolvedCityHandler(UnresolvedCityTriage triage)
    : ICommandHandler<RestoreUnresolvedCityCommand, UnresolvedCityListItemDto>
{
    public Task<UnresolvedCityListItemDto> HandleAsync(RestoreUnresolvedCityCommand command, CancellationToken cancellationToken) =>
        triage.ApplyAsync(
            command.TenantId, command.Id, GeographyAuditActions.UnresolvedCityRestored, (row, now) => row.Restore(now),
            cancellationToken);
}

/// <summary>
/// Lo común a descartar y restaurar: autorización de operador primero, la transición del dominio (<c>not_pending</c> /
/// <c>not_discarded</c>, 422), la auditoría en el tenant operador y el guardado, con <c>Version</c> como token de concurrencia
/// (412 si otro la cambió entre la lectura y el guardado). Devuelve la fila como la pinta la tabla.
/// </summary>
public sealed class UnresolvedCityTriage(
    IUnresolvedCityRepository unresolvedCities,
    IDepartmentRepository departments,
    ICityRepository cities,
    IGeographyUnitOfWork unitOfWork,
    IGeographyAuditRecorder audit,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    IClock clock)
{
    internal async Task<UnresolvedCityListItemDto> ApplyAsync(
        TenantId tenantId,
        UnresolvedCityId id,
        string auditAction,
        Action<UnresolvedCity, DateTimeOffset> transition,
        CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, tenantId, GeographyOperatorPermissions.CitiesManage, operatorTenant);

        var row = ResolvePrecheck.Found(await unresolvedCities.FindAsync(id, cancellationToken));
        var from = row.Status;
        var now = clock.UtcNow;
        transition(row, now);
        audit.Record(
            tenantId.Value,
            executionContext.SubjectId,
            AuditActorType.Human,
            auditAction,
            GeographyAuditActions.UnresolvedCityResourceType,
            row.Id.Value.ToString(),
            JsonSerializer.Serialize(new
            {
                departmentId = row.DepartmentId.Value,
                normalizedText = row.NormalizedText,
                from = from.ToString(),
                to = row.Status.ToString(),
            }),
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var department = await departments.FindAsync(row.DepartmentId, cancellationToken)
            ?? throw new InvalidOperationException($"The department '{row.DepartmentId}' of unresolved city '{row.Id}' does not exist.");
        var departmentCities = await cities.ListByDepartmentAsync(row.DepartmentId, cancellationToken);
        var occurrences = await unresolvedCities.ListOccurrencesAsync(row.Id, cancellationToken);
        return UnresolvedCityViews.ListItem(UnresolvedCityViews.Row(row, occurrences), department, departmentCities);
    }
}
