using System.Buffers.Binary;
using System.Buffers.Text;

namespace Modules.Ai.Application;

/// <summary>
/// Plan 2026-11-17 A5: la posición de una sesión en el orden <c>(started_at, id)</c> del historial, de la más reciente a la más vieja. Copia
/// de <c>ConversationCursor</c> de Mensajería (Ai no la ve): viaja como texto opaco (base64url de los ticks UTC y el id), así que el formato se
/// puede cambiar sin tocar el frontend, que sólo lo devuelve tal cual.
/// </summary>
public sealed record AgentSessionCursor(DateTimeOffset StartedAt, Guid Id)
{
    private const int Length = sizeof(long) + 16;

    public static string Encode(AgentSessionCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        Span<byte> bytes = stackalloc byte[Length];
        BinaryPrimitives.WriteInt64BigEndian(bytes, cursor.StartedAt.UtcTicks);
        cursor.Id.TryWriteBytes(bytes[sizeof(long)..], bigEndian: true, out _);
        return Base64Url.EncodeToString(bytes);
    }

    /// <summary><c>false</c> para todo lo que no salió de <see cref="Encode"/>: el validador lo convierte en <c>validation.failed</c> con la
    /// clave <c>cursor</c>.</summary>
    public static bool TryDecode(string? value, out AgentSessionCursor cursor)
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

        cursor = new AgentSessionCursor(new DateTimeOffset(ticks, TimeSpan.Zero), new Guid(bytes.Slice(sizeof(long), 16), bigEndian: true));
        return true;
    }
}
