using System.Buffers.Binary;
using System.Buffers.Text;

namespace Modules.Messaging.Application;

/// <summary>La posición de una conversación en el orden <c>(last_activity_at, id)</c> de la bandeja, la misma clave de
/// <c>IX_conversations_tenant_status_activity</c>. Viaja como texto opaco (base64url de los ticks UTC y el id): el cliente
/// sólo lo devuelve tal cual, así que el formato se puede cambiar sin tocar el frontend.</summary>
public sealed record ConversationCursor(DateTimeOffset LastActivityAt, Guid Id)
{
    private const int Length = sizeof(long) + 16;

    public static string Encode(ConversationCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        Span<byte> bytes = stackalloc byte[Length];
        BinaryPrimitives.WriteInt64BigEndian(bytes, cursor.LastActivityAt.UtcTicks);
        cursor.Id.TryWriteBytes(bytes[sizeof(long)..], bigEndian: true, out _);
        return Base64Url.EncodeToString(bytes);
    }

    /// <summary><c>false</c> para todo lo que no salió de <see cref="Encode"/>: el validador lo convierte en
    /// <c>validation.failed</c> con la clave <c>cursor</c>.</summary>
    public static bool TryDecode(string? value, out ConversationCursor cursor)
    {
        cursor = null!;
        if (string.IsNullOrEmpty(value) || value.Length > 64)
        {
            return false;
        }

        // TryDecodeFromChars lanza (no devuelve false) ante una longitud imposible: IsValid lo filtra antes.
        Span<byte> bytes = stackalloc byte[Length];
        if (!Base64Url.IsValid(value, out var decodedLength) || decodedLength != Length
            || !Base64Url.TryDecodeFromChars(value, bytes, out var written) || written != Length)
        {
            return false;
        }

        var ticks = BinaryPrimitives.ReadInt64BigEndian(bytes);
        if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
        {
            return false;
        }

        cursor = new ConversationCursor(new DateTimeOffset(ticks, TimeSpan.Zero), new Guid(bytes.Slice(sizeof(long), 16), bigEndian: true));
        return true;
    }
}
