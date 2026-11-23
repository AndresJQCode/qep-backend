using BuildingBlocks.Application;
using FluentValidation;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <summary>El cuerpo de <c>POST /tasks</c> (spec 2026-11-22 §5.2): todo anulable para que su ausencia sea un <c>validation.failed</c>
/// con el campo. <c>Priority</c> ausente es <c>Medium</c>; <c>DueOn</c> ausente es una tarea sin fecha.</summary>
public sealed record CreateTaskRequest(string? Subject, string? Description, Guid? AssigneeMemberId, string? Priority, string? DueOn);

public sealed record CreateTaskCommand(
    Guid TenantId, string Subject, string Description, Guid AssigneeMemberId, string? Priority, string? DueOn)
    : ICommand<SupportTicketDetailDto>
{
    public static CreateTaskCommand From(Guid tenantId, CreateTaskRequest request) => new(
        tenantId,
        request.Subject ?? string.Empty,
        request.Description ?? string.Empty,
        request.AssigneeMemberId ?? Guid.Empty,
        request.Priority,
        request.DueOn);
}

public sealed class CreateTaskValidator : AbstractValidator<CreateTaskCommand>
{
    public CreateTaskValidator()
    {
        RuleFor(command => command.Subject).MustFit(1, SupportTicket.SubjectMaxLength);
        RuleFor(command => command.Description).MustFit(1, SupportTicket.DescriptionMaxLength);
        RuleFor(command => command.AssigneeMemberId).NotEqual(Guid.Empty);
        RuleFor(command => command.Priority).MustBeAPriority();
        // Sólo la forma: que no sea anterior a hoy en la zona del tenant lo juzga el dominio (internal_due_in_past).
        RuleFor(command => command.DueOn)
            .Must(text => text is null || GetSupportReportValidator.TryParse(text, out _))
            .WithMessage("DueOn is a date as YYYY-MM-DD.");
    }
}

/// <summary>
/// El alta de una tarea interna (spec 2026-11-22 §5.2). Orden: acceso (403 sin <c>manage</c> ni <c>task.use</c>: leer sola no crea) →
/// membresía activa de quien actúa (403) → validador → responsable (422 <c>assignee_invalid</c>) → provisión y tipo interno →
/// transacción: radicado de la misma secuencia del tenant, <c>CreateInternal</c> (aquí se juzga la fecha), auditoría y, si el responsable
/// no es quien crea, el evento <c>task-assigned</c> → confirmar → aviso en tiempo real → detalle. Una tarea no tiene PIN, clave de
/// consulta ni correo de apertura (D7).
/// </summary>
public sealed class CreateTaskHandler(
    IExecutionContext executionContext,
    SupportTicketScope scope,
    ISupportProvisioner provisioner,
    ISupportTicketTypeRepository types,
    ISupportTicketRepository tickets,
    ISupportMemberDirectory members,
    ISupportTicketNumberGenerator numbers,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    ISupportIntegrationEvents integrationEvents,
    ISupportRealtime realtime,
    SupportTicketDtoComposer composer,
    IValidator<CreateTaskCommand> validator)
    : ICommandHandler<CreateTaskCommand, SupportTicketDetailDto>
{
    private static readonly string[] CreatedFields = ["subject", "description", "assigneeMemberId", "priority", "dueOn"];

    public async Task<SupportTicketDetailDto> HandleAsync(CreateTaskCommand command, CancellationToken cancellationToken)
    {
        var access = await scope.EnsureAccessAsync(command.TenantId, cancellationToken);
        if (!(access.Grants.Manage || access.Grants.TaskUse))
        {
            throw SupportAuthorization.Denied();
        }

        var actor = access.MemberId ?? throw SupportAuthorization.Denied();
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        await SupportTicketRules.EnsureTaskAssigneeAsync(members, command.TenantId, command.AssigneeMemberId, cancellationToken);

        // Las dos provisiones abren su propia transacción: van antes de BeginTransactionAsync. El tipo interno se asegura ANTES de
        // buscarlo: un tenant aprovisionado entre la migración y hoy todavía no lo tiene. Se lee primero y sólo se siembra si falta:
        // lo normal es que ya exista, y así el alta no abre una transacción con un INSERT en cada llamada.
        await provisioner.EnsureAsync(command.TenantId, cancellationToken);
        var type = await types.FindInternalAsync(command.TenantId, cancellationToken);
        if (type is null)
        {
            await provisioner.EnsureInternalTypeAsync(command.TenantId, cancellationToken);
            type = await types.FindInternalAsync(command.TenantId, cancellationToken);
        }

        if (type is null)
        {
            throw new InvalidOperationException($"Tenant '{command.TenantId}' has no internal ticket type after provisioning it.");
        }

        // Ya validada la forma: TryParse no falla acá.
        DateOnly? dueOn = command.DueOn is { } text && GetSupportReportValidator.TryParse(text, out var date) ? date : null;
        SupportReadContext context;
        SupportTicket ticket;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            var number = await numbers.NextAsync(command.TenantId, cancellationToken);
            context = await composer.ReadContextAsync(command.TenantId, cancellationToken);
            var now = context.Now;
            ticket = SupportTicket.CreateInternal(
                SupportTicketId.New(),
                command.TenantId,
                number,
                type,
                command.Subject,
                command.Description,
                SupportPriorityRules.Parse(command.Priority),
                command.AssigneeMemberId,
                dueOn,
                actor,
                now,
                context.Clock);
            tickets.Add(ticket);
            auditPublisher.Publish(
                command.TenantId,
                executionContext.SubjectId,
                SupportAuditActions.TicketCreated,
                SupportAuditActions.Resources.Ticket,
                ticket.Id.ToString(),
                "success",
                CreatedFields,
                now);
            if (command.AssigneeMemberId != actor)
            {
                integrationEvents.PublishTaskAssigned(ticket, actor, now);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await SupportTicketNotices.ChangedAsync(realtime, ticket, formerAssigneeMemberId: null, CancellationToken.None);
        return await composer.ComposeDetailAsync(ticket, context, cancellationToken);
    }
}
