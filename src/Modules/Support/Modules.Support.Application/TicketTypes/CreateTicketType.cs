using BuildingBlocks.Application;
using FluentValidation;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <summary>El cuerpo de alta y edición de un tipo, todo anulable para que su ausencia sea un <c>validation.failed</c> con el campo y
/// no un 400 del binding. El alta ignora <c>IsActive</c>: un tipo nace activo. <c>FormalNextStep</c> (fase 4b, H6) es opcional: ausente
/// o null es «sin texto», y en la edición lo borra, porque el <c>PUT</c> reemplaza el tipo entero.</summary>
public sealed record TicketTypeRequest(
    string? Name,
    int? TermBusinessDays,
    bool? TermIsLegal,
    bool? RequiresFormalResponse,
    bool? IsPublished,
    bool? IsActive,
    string? FormalNextStep = null);

public sealed record CreateTicketTypeCommand(
    Guid TenantId,
    string Name,
    int? TermBusinessDays,
    bool? TermIsLegal,
    bool? RequiresFormalResponse,
    bool? IsPublished,
    string? FormalNextStep)
    : ICommand<SupportTicketTypeDto>
{
    public static CreateTicketTypeCommand From(Guid tenantId, TicketTypeRequest request) => new(
        tenantId,
        request.Name ?? string.Empty,
        request.TermBusinessDays,
        request.TermIsLegal,
        request.RequiresFormalResponse,
        request.IsPublished,
        request.FormalNextStep);
}

public sealed class CreateTicketTypeValidator : AbstractValidator<CreateTicketTypeCommand>
{
    public CreateTicketTypeValidator()
    {
        RuleFor(command => command.Name).MustBeATypeName();
        RuleFor(command => command.TermBusinessDays).MustBeATerm();
        RuleFor(command => command.TermIsLegal).MustBeLegalOnlyWithTerm(command => command.TermBusinessDays);
        RuleFor(command => command.RequiresFormalResponse).NotNull();
        RuleFor(command => command.IsPublished).NotNull();
        RuleFor(command => command.FormalNextStep).MustBeAFormalNextStep();
    }
}

/// <summary>El tipo nuevo va al final de la lista (<c>Position</c> = la mayor + 1) y activo. El nombre repetido lo frena el índice
/// <c>IX_ticket_types_tenant_name</c> (409 <c>support.ticket_type.name_taken</c>), no una consulta previa. Dos altas a la vez se
/// serializan con el candado de la fila de ajustes del tenant (<see cref="ISupportSettingsRepository.LockAsync"/>, el mismo del
/// radicado), tomado antes de leer la posición mayor: sin él las dos leerían la misma y compartirían posición (revisión final, m5).</summary>
public sealed class CreateTicketTypeHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    ISupportProvisioner provisioner,
    ISupportSettingsRepository settings,
    ISupportTicketTypeRepository types,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    IValidator<CreateTicketTypeCommand> validator)
    : ICommandHandler<CreateTicketTypeCommand, SupportTicketTypeDto>
{
    private static readonly string[] CreatedFields =
        ["name", "termBusinessDays", "termIsLegal", "requiresFormalResponse", "isPublished", "formalNextStep"];

    public async Task<SupportTicketTypeDto> HandleAsync(CreateTicketTypeCommand command, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, SupportPermissions.SettingsManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        await provisioner.EnsureAsync(command.TenantId, cancellationToken);

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        SupportTicketType type;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            await settings.LockAsync(command.TenantId, cancellationToken);
            // Leída ya con el candado: en READ COMMITTED esta lectura ve el tipo que confirmó el alta que esperábamos.
            var existing = await types.ListAsync(command.TenantId, cancellationToken);
            // ListAsync no trae el tipo interno: su posición cuenta igual, o el tipo nuevo empataría con ella (un tenant de ocho tipos
            // tiene el interno en la 9).
            var internalType = await types.FindInternalAsync(command.TenantId, cancellationToken);
            var lastPosition = Math.Max(existing.Count == 0 ? 0 : existing.Max(item => item.Position), internalType?.Position ?? 0);
            type = SupportTicketType.Create(
                SupportTicketTypeId.New(),
                command.TenantId,
                command.Name,
                command.TermBusinessDays,
                command.TermIsLegal!.Value,
                command.RequiresFormalResponse!.Value,
                command.IsPublished!.Value,
                lastPosition + 1,
                now,
                command.FormalNextStep);
            types.Add(type);
            auditPublisher.Publish(
                command.TenantId,
                executionContext.SubjectId,
                SupportAuditActions.TicketTypeCreated,
                SupportAuditActions.Resources.TicketType,
                type.Id.ToString(),
                "success",
                CreatedFields,
                now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // Recién creado: ningún ticket lo usa todavía.
        return SupportDtoMapping.ToDto(type, inUse: false);
    }
}
