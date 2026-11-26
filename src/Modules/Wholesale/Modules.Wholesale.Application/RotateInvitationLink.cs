using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Application;

/// <summary>«Copiar» el link de un distribuidor invitado (spec 2026-11-19 §1).</summary>
public sealed record RotateInvitationLinkCommand(
    Guid TenantId,
    Guid DistributorId,
    long ExpectedVersion,
    string CorrelationId)
    : ICommand<DistributorDetailDto>;

/// <summary>
/// Como el token vive hasheado, «Copiar» genera un link nuevo: rota token y vigencia, el anterior pasa a 404, y la URL vuelve
/// en la respuesta una sola vez (<see cref="DistributorDetailDto.InvitationUrl"/>). No publica eventos ni toca el aviso: no
/// manda nada a nadie. El backend no decide si la invitación «ya se mandó»; ese aviso es del frontend. Orden: 403 → 404 →
/// 412 → 409 <c>invalid_transition</c> fuera de <c>Invited</c>. La auditoría no nombra el token ni su hash.
/// </summary>
public sealed class RotateInvitationLinkHandler(
    IExecutionContext executionContext,
    IClock clock,
    IDistributorRepository distributors,
    IWholesaleUnitOfWork unitOfWork,
    IWholesaleAuditPublisher auditPublisher,
    IWholesaleInvitationLinks links,
    DistributorComposer composer)
    : ICommandHandler<RotateInvitationLinkCommand, DistributorDetailDto>
{
    private static readonly string[] RotatedFields = ["invitation"];

    public async Task<DistributorDetailDto> HandleAsync(RotateInvitationLinkCommand command, CancellationToken cancellationToken)
    {
        WholesaleAuthorization.EnsureAuthorized(executionContext, command.TenantId, WholesalePermissions.DistributorManage);
        var distributor = await distributors.GetAsync(command.TenantId, new DistributorId(command.DistributorId), cancellationToken)
            ?? throw WholesaleConflicts.DistributorNotFound(command.DistributorId);
        WholesaleConflicts.EnsureVersion(distributor.Version, command.ExpectedVersion);
        if (distributor.Status != DistributorStatus.Invited)
        {
            throw WholesaleConflicts.InvalidTransition();
        }

        var now = clock.UtcNow;
        var token = InvitationTokens.Generate();
        distributor.RotateInvitationLink(InvitationTokens.HashOf(token), now);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            WholesaleAuditActions.DistributorInvitationRotated,
            WholesaleAuditActions.Resources.Distributor,
            distributor.Id.ToString(),
            "success",
            RotatedFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var detail = await composer.DetailAsync(distributor, cancellationToken);
        return detail with { InvitationUrl = links.Compose(token) };
    }
}
