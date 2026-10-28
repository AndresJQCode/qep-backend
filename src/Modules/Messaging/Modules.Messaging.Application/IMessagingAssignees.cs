namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-10 §5.1: un ítem de <c>GET /messaging/assignees</c>. <c>DisplayName</c> nunca es <c>null</c>
/// (P17: nombre de la membresía o, si no tiene, el correo).</summary>
public sealed record AssigneeDto(Guid MemberId, string DisplayName);

public sealed record AssigneesDto(IReadOnlyList<AssigneeDto> Items);

/// <summary>
/// Spec 2026-10-10 §6.1.3: quién puede responder en el tenant. Adaptador en Bootstrapper sobre Tenancy, Identity y
/// Authorization (Messaging no los referencia). Por pedido, sin caché: los permisos se resuelven por request en este
/// repo y un cambio de rol tiene que verse ya.
/// </summary>
public interface IMessagingAssignees
{
    /// <summary>La membresía existe <b>en ese tenant</b>, está activa y sus roles conceden
    /// <c>messaging.conversation.manage</c>. Una de otro tenant responde igual que una inexistente (§11).</summary>
    Task<bool> CanReplyAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken);

    /// <summary>Las que cumplen <see cref="CanReplyAsync"/>, ordenadas por nombre.</summary>
    Task<IReadOnlyList<AssigneeDto>> ListAsync(Guid tenantId, CancellationToken cancellationToken);
}
