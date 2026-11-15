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
/// valor no hace nada. Prenderlo en un ticket cerrado es 422 <c>support.ticket.follow_closed</c> (ruling A-c2: no podría vincular nada) y
/// en uno sin conversación, 422 <c>support.ticket.no_conversation</c>.
/// </summary>
public sealed class SetTicketFollowThreadHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IMembershipDirectory membershipDirectory,
    ISupportTicketRepository tickets,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    SupportTicketDtoComposer composer,
    IValidator<SetTicketFollowThreadCommand> validator)
    : ICommandHandler<SetTicketFollowThreadCommand, SupportTicketDetailDto>
{
    private static readonly string[] ChangedFields = ["followThread"];

    public async Task<SupportTicketDetailDto> HandleAsync(SetTicketFollowThreadCommand command, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, SupportPermissions.Manage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var follow = command.Follow ?? throw new InvalidOperationException("Follow passed validation but is null.");
        var (ticket, actor) = await SupportTicketActions.LoadAsync(
            executionContext, membershipDirectory, tickets, command.TenantId, command.TicketId, command.ExpectedVersion, cancellationToken);
        if (follow && ticket.Status is SupportTicketStatus.Closed)
        {
            throw new SupportDomainException(SupportErrorCodes.FollowClosed, "A closed ticket cannot follow its thread again.");
        }

        var context = await composer.ReadContextAsync(command.TenantId, cancellationToken);
        var version = ticket.Version;
        ticket.SetFollowThread(follow, actor, context.Now);
        await SupportTicketActions.SaveIfChangedAsync(
            ticket, version, executionContext, auditPublisher, unitOfWork, SupportAuditActions.TicketFollowChanged, ChangedFields,
            context.Now, cancellationToken);
        return await composer.ComposeDetailAsync(ticket, context, cancellationToken);
    }
}
