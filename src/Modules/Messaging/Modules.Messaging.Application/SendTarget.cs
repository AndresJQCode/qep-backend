namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-10 §6.1.4: a quién se le manda. Con BSUID va <c>recipient</c>; una fila vieja sin BSUID va con
/// <c>to</c>. Nunca los dos: si llegan los dos, Meta usa <c>to</c> (§3) y el BSUID quedaría ignorado.
/// </summary>
public abstract record SendTarget
{
    public static SendTarget For(string? userId, string? waId) =>
        userId is not null ? new SendToUserId(userId)
        : waId is not null ? new SendToPhone(waId)
        : throw new InvalidOperationException("A conversation has a user id or a wa_id (CK_conversations_identity).");
}

public sealed record SendToUserId(string UserId) : SendTarget;

public sealed record SendToPhone(string WaId) : SendTarget;
