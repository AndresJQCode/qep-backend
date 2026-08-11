namespace Modules.Tenancy.Application;

/// <summary>
/// Contrato publicado entre módulos que acepta las invitaciones pendientes de un usuario al
/// hacer login. Según el ADR 0016, el primer login externo exitoso pasa las membresías
/// <c>Invited</c> del usuario a <c>Active</c>. Las invitaciones vencidas se saltean (y se
/// marcan vencidas). Lo consume el endpoint <c>/auth/session</c> de la raíz de composición.
/// </summary>
public interface IMembershipActivation
{
    /// <returns>Los ids de tenant en los que el usuario queda activo tras la aceptación.</returns>
    Task<IReadOnlyCollection<Guid>> AcceptInvitedMembershipsAsync(
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken);
}
