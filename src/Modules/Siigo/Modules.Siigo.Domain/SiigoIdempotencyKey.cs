using System.Text;

namespace Modules.Siigo.Domain;

/// <summary>
/// Spec 2026-11-18 §5.4: la <c>Idempotency-Key</c> de una factura sale del id de la fila, en base32 (RFC 4648) en minúsculas y sin
/// relleno, de los 16 bytes del GUID: 26 caracteres alfanuméricos, dentro del máximo de 30 de Siigo. Es única por intento (por fila), no
/// por pedido: el candado del pedido y la regla de la fila vigente dan la unicidad por pedido, y la clave da la seguridad del reintento.
/// </summary>
public static class SiigoIdempotencyKey
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    public static string For(Guid rowId)
    {
        // Orden de red, para que la misma fila dé la misma clave en cualquier máquina.
        Span<byte> bytes = stackalloc byte[16];
        rowId.TryWriteBytes(bytes, bigEndian: true, out _);

        var builder = new StringBuilder(26);
        var buffer = 0;
        var bits = 0;
        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                builder.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }

            buffer &= (1 << bits) - 1;
        }

        if (bits > 0)
        {
            builder.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return builder.ToString();
    }
}
