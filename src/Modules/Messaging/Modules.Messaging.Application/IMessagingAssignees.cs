namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-10 §5.1: quien puede responder en el tenant. <c>DisplayName</c> nunca es <c>null</c>
/// (P17: nombre de la membresía o, si no tiene, el correo).</summary>
public sealed record AssigneeDto(Guid MemberId, string DisplayName);

/// <summary>Un ítem de <c>GET /messaging/assignees</c>. <c>IsMe</c> sigue la regla de <c>AssignedToDto.IsMe</c> (D-F1): el SPA
/// no conoce su propia membresía, y el diálogo de transferir no ofrece a quien llama (para quedarse con la conversación
/// está «Tomar»).</summary>
public sealed record AssigneeItemDto(Guid MemberId, string DisplayName, bool IsMe);

public sealed record AssigneesDto(IReadOnlyList<AssigneeItemDto> Items);

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
