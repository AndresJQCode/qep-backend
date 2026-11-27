using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wms.Domain.Control;
using static Modules.Wms.Application.WmsAuditActions;

namespace Modules.Wms.Application;

public sealed record RejectIncidentCommand(Guid TenantId, Guid IncidentId, long Version, string Note) : ICommand<IncidentDto>
{
    public static RejectIncidentCommand From(Guid tenantId, Guid incidentId, RejectIncidentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Una nota ausente llega null: se vuelve vacía para que la atrape el validador.
        return new(tenantId, incidentId, request.Version, request.Note ?? string.Empty);
    }
}

/// <summary>El motivo es texto libre: el dominio ya lo valida, pero con un código; el validador da el campo.</summary>
public sealed class RejectIncidentValidator : AbstractValidator<RejectIncidentCommand>
{
    public RejectIncidentValidator()
    {
        RuleFor(command => command.Note)
            .Must(note => !string.IsNullOrWhiteSpace(note) && note.Trim().Length <= Incident.NoteMaxLength)
            .WithMessage($"The note is required and has at most {Incident.NoteMaxLength} characters.");
    }
}

/// <summary>Descarta una novedad que no movió nada (hoy sólo <c>SyncConflict</c>; el dominio lo decide con el catálogo). La nota
/// es obligatoria y viaja a la auditoría.</summary>
public sealed class RejectIncidentHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    WmsCaller caller,
    IIncidentRepository incidents,
    IWmsUnitOfWork unitOfWork,
    IWmsAuditRecorder auditRecorder,
    IClock clock,
    IncidentDtoComposer composer,
    IValidator<RejectIncidentCommand> validator)
    : ICommandHandler<RejectIncidentCommand, IncidentDto>
{
    public async Task<IncidentDto> HandleAsync(RejectIncidentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await authorization.EnsureAsync(executionContext, command.TenantId, WmsPermissions.DiscrepancyResolve, ct: cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

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
        incident.Reject(await caller.RequireAsync(command.TenantId, cancellationToken), command.Note, now);
        auditRecorder.Record(
            command.TenantId,
            executionContext.SubjectId,
            WmsActorType.Human,
            IncidentRejected,
            Resources.Incident,
            incident.Id.ToString(),
            new Dictionary<string, string?>
            {
                ["ownerId"] = incident.OwnerId.Value.ToString(),
                ["number"] = incident.Number,
                ["type"] = incident.Type.ToString(),
                ["note"] = incident.ResolutionNote,
            },
            now);

        var dto = await composer.ComposeAsync(incident, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return dto;
    }
}
