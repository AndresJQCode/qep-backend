namespace Modules.Identity.Application;

/// <summary>
/// Resultado de un intento de vincular un login externo. Se setea exactamente uno de
/// <see cref="UserId"/> o <see cref="DenialReason"/>.
/// </summary>
public sealed record ProviderLinkOutcome(Guid? UserId, string? DenialReason)
{
    public static ProviderLinkOutcome Linked(Guid userId) => new(userId, null);

    public static ProviderLinkOutcome Denied(string reason) => new(null, reason);

    public bool IsDenied => DenialReason is not null;
}

/// <summary>
/// Contrato publicado entre módulos que resuelve una identidad de proveedor externo a un
/// usuario interno, aplicando las reglas del ADR 0015: aprovisionamiento sólo-por-invitación,
/// vincular cuando coincide un email verificado, denegar para emails desconocidos o no
/// verificados. Lo usa el endpoint <c>/auth/session</c> de composición en el primer login.
/// </summary>
public interface IProviderLinking
{
    Task<ProviderLinkOutcome> LinkAndActivateAsync(
        string provider,
        string subject,
        string? email,
        bool emailVerified,
        CancellationToken cancellationToken);
}
