using BuildingBlocks.Application;
using FluentValidation;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

public sealed record SetTicketFollowThreadRequest(bool? Follow);

/// <param name="ExpectedVersion">La versión que la pantalla cargó; llega por <c>If-Match</c>.</param>
public sealed record SetTicketFollowThreadCommand(Guid TenantId, Guid TicketId, bool? Follow, long ExpectedVersion)
    : ICommand<SupportTicketDetailDto>
{
    public static SetTicketFollowThreadCommand From(
        Guid tenantId, Guid ticketId, SetTicketFollowThreadRequest request, long expectedVersion) =>
        new(tenantId, ticketId, request.Follow, expectedVersion);
}

public sealed class SetTicketFollowThreadValidator : AbstractValidator<SetTicketFollowThreadCommand>
{
    public SetTicketFollowThreadValidator()
    {
        RuleFor(command => command.Follow).NotNull();
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

/// <summary>
/// «Seguir el hilo» (spec §8.2, plan E4): apagarlo sella la fecha de la franja; prenderlo abre una ventana nueva desde ahora. El mismo
/// valor no hace nada. En un ticket cerrado, prenderlo o apagarlo es 422 <c>support.ticket.not_editable</c>, lo guarda el agregado (R9
/// extendida, owner 2026-11-15; antes del ruling era <c>follow_closed</c>, ruling A-c2); en uno resuelto se permite. Sin conversación,
/// 422 <c>support.ticket.no_conversation</c>.
/// </summary>
public sealed class SetTicketFollowThreadHandler(
    IExecutionContext executionContext,
    SupportTicketScope scope,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    ISupportRealtime realtime,
    SupportTicketDtoComposer composer,
    IValidator<SetTicketFollowThreadCommand> validator)
    : ICommandHandler<SetTicketFollowThreadCommand, SupportTicketDetailDto>
{
    private static readonly string[] ChangedFields = ["followThread"];

    public async Task<SupportTicketDetailDto> HandleAsync(SetTicketFollowThreadCommand command, CancellationToken cancellationToken)
    {
        await scope.EnsureAccessAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var follow = command.Follow ?? throw new InvalidOperationException("Follow passed validation but is null.");
        var (ticket, actor) = await scope.ManageAsync(command.TenantId, command.TicketId, cancellationToken);
        SupportTicketScope.EnsureExternal(ticket);
        SupportTicketScope.EnsureVersion(ticket, command.ExpectedVersion);
        var context = await composer.ReadContextAsync(command.TenantId, cancellationToken);
        var version = ticket.Version;
        ticket.SetFollowThread(follow, actor, context.Now);
        await SupportTicketActions.SaveIfChangedAsync(
            ticket, version, executionContext, auditPublisher, unitOfWork, SupportAuditActions.TicketFollowChanged, ChangedFields,
            context.Now, cancellationToken);
        if (ticket.Version != version)
        {
            await realtime.TicketChangedAsync(command.TenantId, ticket.Id.Value, CancellationToken.None);
        }

        return await composer.ComposeDetailAsync(ticket, context, cancellationToken);
    }
}
