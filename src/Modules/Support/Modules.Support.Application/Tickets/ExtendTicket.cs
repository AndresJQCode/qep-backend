using BuildingBlocks.Application;
using FluentValidation;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <summary>Fase 4a (spec §7.2, plan G9): <c>BusinessDays</c> son los días hábiles que se suman al plazo; <c>Reason</c>, el motivo
/// que queda en la traza.</summary>
public sealed record ExtendTicketRequest(int? BusinessDays, string? Reason);

/// <param name="ExpectedVersion">La versión que la pantalla cargó; llega por <c>If-Match</c>.</param>
public sealed record ExtendTicketCommand(Guid TenantId, Guid TicketId, int BusinessDays, string Reason, long ExpectedVersion)
    : ICommand<SupportTicketDetailDto>
{
    public static ExtendTicketCommand From(Guid tenantId, Guid ticketId, ExtendTicketRequest request, long expectedVersion) =>
        new(tenantId, ticketId, request.BusinessDays ?? 0, request.Reason ?? string.Empty, expectedVersion);
}

/// <summary>Plan G11: los días de 1 a <see cref="SupportTicketType.MaxTermBusinessDays"/> y el motivo de 1 a
/// <see cref="SupportTicket.ExtensionReasonMaxLength"/> caracteres, recortado. El tope real (no pasar del término del ticket) lo juzga
/// el dominio con <c>support.ticket.extension_too_long</c>.</summary>
public sealed class ExtendTicketValidator : AbstractValidator<ExtendTicketCommand>
{
    public ExtendTicketValidator()
    {
        RuleFor(command => command.BusinessDays).InclusiveBetween(1, SupportTicketType.MaxTermBusinessDays);
        RuleFor(command => command.Reason)
            .Must(text => text.Trim().Length is > 0 and <= SupportTicket.ExtensionReasonMaxLength)
            .WithMessage($"Reason must have between 1 and {SupportTicket.ExtensionReasonMaxLength} characters.");
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

/// <summary>
/// Fase 4a (spec §7.2, plan G9 a G13): la prórroga única del plazo, antes de que venza y con motivo. Las reglas y su orden viven en
/// <c>SupportTicket.Extend</c>; el handler sigue el orden de <see cref="AssignTicketHandler"/>. Audita
/// <c>support.ticket.extended</c> con los nombres de campo, nunca el motivo (plan G11), y avisa al tiempo real después de confirmar.
/// El aviso al solicitante no sale de acá: lo manda el asesor como respuesta (plan G13).
/// </summary>
public sealed class ExtendTicketHandler(
    IExecutionContext executionContext,
    SupportTicketScope scope,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    ISupportRealtime realtime,
    SupportTicketDtoComposer composer,
    IValidator<ExtendTicketCommand> validator)
    : ICommandHandler<ExtendTicketCommand, SupportTicketDetailDto>
{
    private static readonly string[] ChangedFields = ["dueOn", "extension"];

    public async Task<SupportTicketDetailDto> HandleAsync(ExtendTicketCommand command, CancellationToken cancellationToken)
    {
        await scope.EnsureAccessAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var (ticket, actor) = await scope.ManageAsync(command.TenantId, command.TicketId, cancellationToken);
        SupportTicketScope.EnsureVersion(ticket, command.ExpectedVersion);
        var context = await composer.ReadContextAsync(command.TenantId, cancellationToken);
        var now = context.Now;
        var version = ticket.Version;
        ticket.Extend(command.BusinessDays, command.Reason, actor, now, context.Clock);
        await SupportTicketActions.SaveIfChangedAsync(
            ticket, version, executionContext, auditPublisher, unitOfWork, SupportAuditActions.TicketExtended, ChangedFields, now,
            cancellationToken);
        if (ticket.Version != version)
        {
            await realtime.TicketChangedAsync(command.TenantId, ticket.Id.Value, CancellationToken.None);
        }

        return await composer.ComposeDetailAsync(ticket, context, cancellationToken);
    }
}
