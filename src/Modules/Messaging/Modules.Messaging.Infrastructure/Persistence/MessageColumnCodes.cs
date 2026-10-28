using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Spec 2026-10-09 §7.1: la única traducción entre los smallint de <c>messages</c> y los enums.
/// Un código desconocido lanza: ignorarlo escondería una base corrupta.</summary>
internal static class MessageColumnCodes
{
    public static short ToCode(MessageDirection value) => (short)value;

    public static short ToCode(MessageKind value) => (short)value;

    public static short ToCode(MessageStatus value) => (short)value;

    public static MessageDirection ToDirection(short code) => Enum.IsDefined((MessageDirection)code)
        ? (MessageDirection)code
        : throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown message direction code.");

    public static MessageKind ToKind(short code) => Enum.IsDefined((MessageKind)code)
        ? (MessageKind)code
        : throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown message kind code.");

    public static MessageStatus ToStatus(short code) => Enum.IsDefined((MessageStatus)code)
        ? (MessageStatus)code
        : throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown message status code.");
}
