using BuildingBlocks.Application;
using Modules.Audit.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// La vista de una invitación resuelta por su token. <see cref="Status"/> es el estado
/// derivado (<see cref="MembershipViewStates.Of"/>): el vencimiento es perezoso y la fila
/// puede seguir <c>Invited</c> con la ventana ya pasada. El email del invitado no está acá
/// a propósito: es de Identity, y la composición entre módulos vive en src/Api.
/// </summary>
public sealed record InvitationDto(
    TenantId TenantId,
    string TenantName,
    Guid UserId,
    MembershipViewState Status);

/// <summary>
/// Contrato publicado que resuelve y acepta invitaciones por el token del email.
/// Complementa el auto-accept del login (<see cref="IMembershipActivation"/>), que sigue
/// intacto: este camino existe para que quien abre el link acepte esa invitación puntual,
/// con la sesión ya establecida.
/// </summary>
public interface IInvitationService
{
    /// <exception cref="ResourceNotFoundException">El hash del token no matchea ninguna membresía.</exception>
    Task<InvitationDto> FindByTokenAsync(string token, CancellationToken cancellationToken);

    /// <exception cref="ResourceNotFoundException">El hash del token no matchea ninguna membresía.</exception>
    /// <exception cref="RequestForbiddenException">La sesión pertenece a otro usuario.</exception>
    /// <exception cref="TenantDomainException">La invitación venció o su estado no admite aceptar.</exception>
    Task AcceptAsync(
        string token,
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken);
}

public sealed class InvitationService(
    IMembershipRepository membershipRepository,
    ITenantRepository tenantRepository,
    ITenancyUnitOfWork unitOfWork,
    IAuditRecorder auditRecorder,
    IOutboxWriter outboxWriter,
    IClock clock) : IInvitationService
{
    public async Task<InvitationDto> FindByTokenAsync(
        string token,
        CancellationToken cancellationToken)
    {
        var membership = await FindMembershipAsync(token, cancellationToken);
        var tenant = await tenantRepository.GetAsync(membership.TenantId, cancellationToken);
        return new InvitationDto(
            membership.TenantId,
            tenant?.DisplayName ?? string.Empty,
            membership.UserId,
            MembershipViewStates.Of(membership.State, membership.ExpiresAt, clock.UtcNow));
    }

    public async Task AcceptAsync(
        string token,
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var membership = await FindMembershipAsync(token, cancellationToken);
        if (membership.UserId != userId)
        {
            // El link identifica una invitación, no autentica a quien lo abre: con la
            // sesión de otra cuenta se rechaza sin tocar nada. El mensaje no dice de quién
            // es — mismo criterio de no filtrar identidades que el 403 de login.
            throw new RequestForbiddenException(
                "tenancy.invitation.user_mismatch",
                "The invitation belongs to a different user.");
        }

        if (membership.State == MembershipState.Active)
        {
            // Idempotente: el auto-accept del login pudo haberla activado antes de que la
            // persona toque el botón. No hay nada nuevo que auditar ni emitir.
            return;
        }

        var now = clock.UtcNow;
        try
        {
            membership.Accept(now);
        }
        catch (TenantDomainException)
        {
            // Vencida: Accept ya la marcó Expired. Se persiste esa transición antes de
            // re-lanzar hacia el mapeo central (422) para que el próximo GET del mismo
            // link lea el estado real y no lo re-derive de una fila Invited fantasma.
            await unitOfWork.SaveChangesAsync(cancellationToken);
            throw;
        }

        // La persona que acepta es la actora de su propia aceptación, igual que en
        // MembershipActivationService; auditoría y outbox commitean con el cambio.
        auditRecorder.Record(
            membership.TenantId.Value,
            userId,
            "tenancy.membership.accepted",
            "membership",
            membership.Id.ToString(),
            "success",
            [],
            now);

        foreach (var domainEvent in membership.PullDomainEvents())
        {
            outboxWriter.Add(domainEvent, correlationId);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task<Membership> FindMembershipAsync(
        string token,
        CancellationToken cancellationToken)
    {
        var membership = await membershipRepository.FindByInvitationTokenHashAsync(
            InvitationTokens.HashOf(token),
            cancellationToken);
        return membership ?? throw new ResourceNotFoundException(
            "tenancy.invitation.not_found",
            "No invitation matches the provided token.");
    }
}
