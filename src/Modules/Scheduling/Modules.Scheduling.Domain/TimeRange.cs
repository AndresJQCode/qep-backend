namespace Modules.Scheduling.Domain;

/// <summary>
/// Minutos locales del día, <c>[From, To)</c>, entre 0 y 1440 (spec 2026-11-21, «Franjas»). El mismo formato de los horarios de atención
/// de Mensajería, declarado acá porque Scheduling no puede depender de Messaging (D-10). Un rango vacío, al revés o fuera del día es un
/// error de programación: el validador de la petición lo rechaza antes con el campo marcado.
/// </summary>
public readonly record struct TimeRange
{
    public const int MinutesInDay = 1440;

    public TimeRange(int from, int to)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(to, MinutesInDay);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(from, to);

        From = from;
        To = to;
    }

    public int From { get; }

    public int To { get; }

    public int Minutes => To - From;

    public bool Contains(int minute) => minute >= From && minute < To;

    /// <summary>Dos rangos que se tocan en un extremo (<c>[8,12)</c> y <c>[12,14)</c>) no se solapan.</summary>
    public bool Overlaps(TimeRange other) => From < other.To && other.From < To;
}
