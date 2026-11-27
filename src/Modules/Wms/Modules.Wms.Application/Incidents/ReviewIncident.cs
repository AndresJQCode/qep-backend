using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using static Modules.Wms.Application.WmsAuditActions;

namespace Modules.Wms.Application;

public sealed record ReviewIncidentCommand(Guid TenantId, Guid IncidentId, long Version) : ICommand<IncidentDto>;

/// <summary>El supervisor toma una novedad (<c>Open</c> pasa a <c>InReview</c>). No toca el ledger: basta la versión como token
/// optimista (dos supervisores a la vez dejan uno ganador y el otro recibe 412). Se audita con el sujeto del supervisor; la historia
/// de la novedad guarda además su membresía.</summary>
public sealed class ReviewIncidentHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    WmsCaller caller,
    IIncidentRepository incidents,
    IWmsUnitOfWork unitOfWork,
    IWmsAuditRecorder auditRecorder,
    IClock clock,
    IncidentDtoComposer composer)
    : ICommandHandler<ReviewIncidentCommand, IncidentDto>
{
    public async Task<IncidentDto> HandleAsync(ReviewIncidentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await authorization.EnsureAsync(executionContext, command.TenantId, WmsPermissions.DiscrepancyResolve, ct: cancellationToken);

        var incident = await incidents.GetAsync(command.TenantId, command.IncidentId, cancellationToken)
            ?? throw WmsNotFound.Incident(command.IncidentId);
        await authorization.EnsureAsync(
            executionContext,
            command.TenantId,
            WmsPermissions.DiscrepancyResolve,
            incident.OwnerId.Value,
            incident.WarehouseId.Value,
            cancellationToken);
        WmsVersions.Ensure(incident.Version, command.Version);

        var now = clock.UtcNow;
        incident.Review(await caller.RequireAsync(command.TenantId, cancellationToken), now);
        auditRecorder.Record(
            command.TenantId,
            executionContext.SubjectId,
            WmsActorType.Human,
            IncidentReviewed,
            Resources.Incident,
            incident.Id.ToString(),
            new Dictionary<string, string?>
            {
                ["ownerId"] = incident.OwnerId.Value.ToString(),
                ["number"] = incident.Number,
                ["type"] = incident.Type.ToString(),
            },
            now);

        var dto = await composer.ComposeAsync(incident, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return dto;
    }
}
