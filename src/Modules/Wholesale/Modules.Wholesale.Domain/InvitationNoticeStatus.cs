namespace Modules.Wholesale.Domain;

/// <summary>
/// El aviso por WhatsApp de una invitación (spec 2026-11-18). <see cref="Pending"/> desde el commit que lo encola hasta que
/// llega el resultado del envío; sin teléfono, o tras un cambio de teléfono o conexión, no hay aviso (<c>null</c>). Se guarda
/// por nombre.
/// </summary>
public enum InvitationNoticeStatus
{
    Pending,
    Sent,
    Failed,
}
