using System.Globalization;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-20 D10: un rango de bytes tal como lo pidió el navegador. <c>From</c> en <c>null</c> = sufijo de <c>To</c>
/// bytes (<c>bytes=-500</c>); <c>To</c> en <c>null</c> = hasta el final (<c>bytes=500-</c>).</summary>
public readonly record struct MediaByteRangeRequest(long? From, long? To);

/// <summary>D10: el tramo que se sirve, inclusive, y el tamaño del objeto (<c>Content-Range: bytes From-To/Total</c>). Uno que no cabe
/// en el objeto lleva <c>From = To = -1</c> y se responde 416 con <c>bytes */Total</c>.</summary>
public sealed record MediaRangeDto(long From, long To, long Total)
{
    public static MediaRangeDto NotSatisfiable(long total) => new(-1, -1, total);

    public bool Satisfiable => From >= 0;

    public long Length => Satisfiable ? To - From + 1 : 0;
}

/// <summary>
/// D10: el <c>Range</c> de <c>GET …/media/{id}</c> (RFC 9110 §14.1.2). Sólo un rango, porque es lo que pide un <c>&lt;video&gt;</c> o un
/// <c>&lt;audio&gt;</c> al adelantar; varios rangos o un valor que no se entiende se ignoran y se sirve el archivo entero (200), que la
/// RFC permite. Sin <c>If-Range</c>: el objeto de un mensaje no cambia una vez servido (un reintento de un <c>Failed</c> lo
/// sobrescribe antes de que nadie lo haya visto entero).
/// </summary>
public static class MediaByteRange
{
    private const string Unit = "bytes";

    /// <summary><c>null</c> si no hay rango o se ignora.</summary>
    public static MediaByteRangeRequest? Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        var equals = header.IndexOf('=', StringComparison.Ordinal);
        if (equals < 0 || !header[..equals].Trim().Equals(Unit, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var spec = header[(equals + 1)..].Trim();
        var dash = spec.IndexOf('-', StringComparison.Ordinal);
        if (spec.Contains(',', StringComparison.Ordinal) || dash < 0)
        {
            return null;
        }

        var first = spec[..dash].Trim();
        var last = spec[(dash + 1)..].Trim();
        long? from = null;
        long? to = null;
        if (first.Length > 0)
        {
            if (!TryParse(first, out var value))
            {
                return null;
            }

            from = value;
        }

        if (last.Length > 0)
        {
            if (!TryParse(last, out var value))
            {
                return null;
            }

            to = value;
        }

        return (from, to) switch
        {
            (null, null) => null,
            ({ } a, { } b) when b < a => null,
            _ => new MediaByteRangeRequest(from, to),
        };
    }

    public static MediaRangeDto Resolve(MediaByteRangeRequest range, long total)
    {
        if (total <= 0)
        {
            return MediaRangeDto.NotSatisfiable(total);
        }

        if (range.From is not { } from)
        {
            // Sufijo: los últimos N bytes; pedir más que el objeto es el objeto entero, pedir cero no se puede servir.
            var suffix = range.To!.Value;
            return suffix == 0 ? MediaRangeDto.NotSatisfiable(total) : new MediaRangeDto(Math.Max(0, total - suffix), total - 1, total);
        }

        return from >= total ? MediaRangeDto.NotSatisfiable(total) : new MediaRangeDto(from, Math.Min(range.To ?? total - 1, total - 1), total);
    }

    private static bool TryParse(string digits, out long value) =>
        long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
