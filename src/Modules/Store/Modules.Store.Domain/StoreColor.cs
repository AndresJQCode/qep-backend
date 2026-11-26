using System.Globalization;

namespace Modules.Store.Domain;

/// <summary>Color sRGB <c>#RRGGBB</c>. Puro: luminancia y contraste son los de WCAG 2.x.</summary>
public readonly record struct StoreColor(byte R, byte G, byte B)
{
    public static readonly StoreColor White = new(255, 255, 255);

    public static bool TryParse(string? raw, out StoreColor color)
    {
        color = default;
        if (raw is not { Length: 7 } || raw[0] != '#')
        {
            return false;
        }

        foreach (var c in raw.AsSpan(1))
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        color = new StoreColor(
            byte.Parse(raw.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(raw.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(raw.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return true;
    }

    public string ToHex() => string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}");

    public double RelativeLuminance() =>
        (0.2126 * Linear(R)) + (0.7152 * Linear(G)) + (0.0722 * Linear(B));

    /// <summary>Razón (L1 + 0.05) / (L2 + 0.05) con el más claro arriba, así que es simétrica.</summary>
    public static double Contrast(StoreColor a, StoreColor b)
    {
        var la = a.RelativeLuminance();
        var lb = b.RelativeLuminance();
        var (lighter, darker) = la >= lb ? (la, lb) : (lb, la);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>Misma tonalidad y saturación en HSL con menos luminosidad (<paramref name="amount"/> absoluto, 0..1).</summary>
    public StoreColor Darken(double amount)
    {
        var (h, s, l) = ToHsl();
        return FromHsl(h, s, Math.Max(0d, l - amount));
    }

    private static double Linear(byte channel)
    {
        var c = channel / 255d;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private (double H, double S, double L) ToHsl()
    {
        double r = R / 255d, g = G / 255d, b = B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2d;
        var d = max - min;
        if (d == 0d)
        {
            return (0d, 0d, l);
        }

        var s = l > 0.5 ? d / (2d - max - min) : d / (max + min);
        double h;
        if (max == r)
        {
            h = ((g - b) / d) + (g < b ? 6d : 0d);
        }
        else if (max == g)
        {
            h = ((b - r) / d) + 2d;
        }
        else
        {
            h = ((r - g) / d) + 4d;
        }

        return (h * 60d, s, l);
    }

    private static StoreColor FromHsl(double h, double s, double l)
    {
        if (s == 0d)
        {
            var v = ToByte(l);
            return new StoreColor(v, v, v);
        }

        var q = l < 0.5 ? l * (1d + s) : l + s - (l * s);
        var p = (2d * l) - q;
        var hk = h / 360d;
        return new StoreColor(
            ToByte(Channel(p, q, hk + (1d / 3d))),
            ToByte(Channel(p, q, hk)),
            ToByte(Channel(p, q, hk - (1d / 3d))));
    }

    private static double Channel(double p, double q, double t)
    {
        if (t < 0d)
        {
            t += 1d;
        }

        if (t > 1d)
        {
            t -= 1d;
        }

        if (t < 1d / 6d)
        {
            return p + ((q - p) * 6d * t);
        }

        if (t < 0.5)
        {
            return q;
        }

        return t < 2d / 3d ? p + ((q - p) * ((2d / 3d) - t) * 6d) : p;
    }

    private static byte ToByte(double unit) =>
        (byte)Math.Clamp((int)Math.Round(unit * 255d, MidpointRounding.AwayFromZero), 0, 255);
}
