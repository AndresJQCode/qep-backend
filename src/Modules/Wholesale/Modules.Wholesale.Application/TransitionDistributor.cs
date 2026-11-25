using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
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
/// cambian el estado. Fuera de su estado: 409 <c>invalid_transition</c>, antes de tocar el dominio. Con teléfono (spec
/// 2026-11-18), reenviar encola un aviso nuevo si la conexión guardada sigue lista; si no, el aviso queda fallido con
/// <c>whatsapp_unavailable</c> y el correo sale igual, también si consultar las conexiones falla.
/// </summary>
public sealed partial class TransitionDistributorHandler(
    IExecutionContext executionContext,
    IClock clock,
    IDistributorRepository distributors,
    IWholesaleWhatsAppConnections whatsAppConnections,
    IMembershipDirectory memberships,
    IWholesaleUnitOfWork unitOfWork,
    IWholesaleAuditPublisher auditPublisher,
    IWholesaleEventPublisher eventPublisher,
    DistributorComposer composer,
    ILogger<TransitionDistributorHandler> logger)
    : ICommandHandler<TransitionDistributorCommand, DistributorDetailDto>
{
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
                var (emailProof, emailProofHash) = DistributorEmailProofs.Issue();
                var noticeQueued = await NoticeConnectionIsReadyAsync(distributor, cancellationToken);
                distributor.ResendInvitation(InvitationTokens.HashOf(token), noticeQueued, now, emailProofHash);
                eventPublisher.DistributorInvited(
                    command.TenantId,
                    distributor.Id.Value,
                    distributor.Email,
                    distributor.BusinessName,
                    token,
                    emailProof,
                    distributor.Invitation.ExpiresAt,
                    command.CorrelationId,
                    now);
                if (noticeQueued)
                {
                    await DistributorWhatsAppNotice.QueueAsync(
                        eventPublisher, memberships, executionContext.SubjectId, distributor, token, command.CorrelationId, now,
                        cancellationToken);
                }

                changedFields = ["invitation", DistributorWhatsAppNotice.AuditFlag(noticeQueued)];
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

    // Sin teléfono no hay aviso y no se consulta Integrations.
    private async Task<bool> NoticeConnectionIsReadyAsync(Distributor distributor, CancellationToken cancellationToken)
    {
        if (distributor.Phone is null || distributor.WhatsAppConnectionId is not { } connectionId)
        {
            return false;
        }

        // Un fallo del aviso nunca frena el correo: si Integrations falla (base, catálogo, un secreto que no descifra), el aviso
        // queda como no listo (Failed whatsapp_unavailable) y el reenvío sigue. El log lleva sólo ids, nunca el teléfono.
        try
        {
            var ready = await whatsAppConnections.ListReadyAsync(distributor.TenantId, cancellationToken);
            return ready.Any(connection => connection.Id == connectionId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogConnectionsUnavailable(logger, exception, distributor.TenantId, distributor.Id.Value, connectionId);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not list the WhatsApp connections ready for the invitation notice of distributor {DistributorId} of tenant {TenantId} (connection {ConnectionId}); the notice fails and the email goes out.")]
    private static partial void LogConnectionsUnavailable(
        ILogger logger, Exception exception, Guid tenantId, Guid distributorId, Guid connectionId);
}
