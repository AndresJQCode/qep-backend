using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Application;

/// <summary>Las tres acciones sin cuerpo: <c>Resend</c>, <c>Block</c> y <c>Unblock</c>.</summary>
public sealed record TransitionDistributorCommand(
    Guid TenantId,
    Guid DistributorId,
    long ExpectedVersion,
    DistributorAction Action,
    string CorrelationId)
    : ICommand<DistributorDetailDto>;

/// <summary>
/// Reenviar rota token y vigencia (el link anterior pasa a 404) y vuelve a publicar el correo; bloquear y desbloquear sólo
/// cambian el estado. Fuera de su estado: 409 <c>invalid_transition</c>, antes de tocar el dominio.
/// </summary>
public sealed class TransitionDistributorHandler(
    IExecutionContext executionContext,
    IClock clock,
    IDistributorRepository distributors,
    IWholesaleUnitOfWork unitOfWork,
    IWholesaleAuditPublisher auditPublisher,
    IWholesaleEventPublisher eventPublisher,
    DistributorComposer composer)
    : ICommandHandler<TransitionDistributorCommand, DistributorDetailDto>
{
    private static readonly string[] InvitationField = ["invitation"];

    private static readonly string[] StatusField = ["status"];

    public async Task<DistributorDetailDto> HandleAsync(TransitionDistributorCommand command, CancellationToken cancellationToken)
    {
        var auditAction = command.Action switch
        {
            DistributorAction.Resend => WholesaleAuditActions.DistributorInvitationResent,
            DistributorAction.Block => WholesaleAuditActions.DistributorBlocked,
            DistributorAction.Unblock => WholesaleAuditActions.DistributorUnblocked,
            _ => throw new ArgumentOutOfRangeException(
                nameof(command), command.Action, "Only resend, block and unblock go through this handler."),
        };

        WholesaleAuthorization.EnsureAuthorized(executionContext, command.TenantId, WholesalePermissions.DistributorManage);
        var distributor = await distributors.GetAsync(command.TenantId, new DistributorId(command.DistributorId), cancellationToken)
            ?? throw WholesaleConflicts.DistributorNotFound(command.DistributorId);
        WholesaleConflicts.EnsureVersion(distributor.Version, command.ExpectedVersion);
        WholesaleConflicts.EnsureAllowed(distributor, command.Action);

        var now = clock.UtcNow;
        string[] changedFields;
        switch (command.Action)
        {
            case DistributorAction.Resend:
                var token = InvitationTokens.Generate();
                distributor.ResendInvitation(InvitationTokens.HashOf(token), noticeQueued: false, now);
                eventPublisher.DistributorInvited(
                    command.TenantId,
                    distributor.Id.Value,
                    distributor.Email,
                    distributor.BusinessName,
                    token,
                    distributor.Invitation.ExpiresAt,
                    command.CorrelationId,
                    now);
                changedFields = InvitationField;
                break;
            case DistributorAction.Block:
                distributor.Block(now);
                changedFields = StatusField;
                break;
            default:
                distributor.Unblock(now);
                changedFields = StatusField;
                break;
        }

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            auditAction,
            WholesaleAuditActions.Resources.Distributor,
            distributor.Id.ToString(),
            "success",
            changedFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.DetailAsync(distributor, cancellationToken);
    }
}
