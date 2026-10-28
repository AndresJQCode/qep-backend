using System.Collections.Concurrent;

namespace Modules.Messaging.Domain;

/// <summary>
/// Festivos de Colombia (Ley 51 de 1983), calculados y no tabulados para que el constructor no los invente (spec 2026-10-21 §5.2).
/// Fijos sin traslado, trasladados al lunes siguiente (Ley Emiliani) y los que dependen de la Pascua.
/// </summary>
public static class ColombianHolidays
{
    // Pura y por año: se calcula una vez (IsOpenAt y NextChangeAt la piden cientos de veces).
    private static readonly ConcurrentDictionary<int, IReadOnlySet<DateOnly>> Cache = new();

    public static IReadOnlySet<DateOnly> For(int year) => Cache.GetOrAdd(year, Compute);

    private static HashSet<DateOnly> Compute(int year)
    {
        var set = new HashSet<DateOnly>
        {
            new(year, 1, 1),
            new(year, 5, 1),
            new(year, 7, 20),
            new(year, 8, 7),
            new(year, 12, 8),
            new(year, 12, 25),
        };

        foreach (var (month, day) in new[] { (1, 6), (3, 19), (6, 29), (8, 15), (10, 12), (11, 1), (11, 11) })
        {
            set.Add(NextMonday(new DateOnly(year, month, day)));
        }

        var easter = EasterSunday(year);
        set.Add(easter.AddDays(-3));
        set.Add(easter.AddDays(-2));
        set.Add(NextMonday(easter.AddDays(39)));
        set.Add(NextMonday(easter.AddDays(60)));
        set.Add(NextMonday(easter.AddDays(68)));
        return set;
    }

    /// <summary>Cómputo gregoriano de Meeus/Jones/Butcher.</summary>
    public static DateOnly EasterSunday(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = ((h + l - 7 * m + 114) % 31) + 1;
        return new DateOnly(year, month, day);
    }

    /// <summary>Las próximas <paramref name="count"/> fechas desde <paramref name="fromInclusive"/>, cruzando el cambio de año.</summary>
    public static IReadOnlyList<DateOnly> Upcoming(DateOnly fromInclusive, int count)
    {
        var result = new List<DateOnly>();
        for (var year = fromInclusive.Year; result.Count < count && year <= fromInclusive.Year + 10; year++)
        {
            result.AddRange(For(year).Where(date => date >= fromInclusive).OrderBy(date => date).Take(count - result.Count));
        }

        return result;
    }

    // Lunes = 0 días; martes = 6; domingo = 1.
    private static DateOnly NextMonday(DateOnly date) => date.AddDays((8 - IsoDay(date)) % 7);

    private static int IsoDay(DateOnly date) => date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek;
}
