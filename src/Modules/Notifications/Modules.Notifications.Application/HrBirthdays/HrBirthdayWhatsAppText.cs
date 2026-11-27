using System.Globalization;

namespace Modules.Notifications.Application;

/// <summary>
/// HR birthday notices, spec 2026-11-26 §5.4: the texts of the WhatsApp template variables. Meta rejects empty variables and line breaks, and a
/// variable has a length limit, so a list never travels empty and folds to «Ana, Luis y 3 más» when it is long. The same date text is used by
/// the email. Spanish and without a year: the notice never says how old anybody is.
/// </summary>
public static class HrBirthdayWhatsAppText
{
    /// <summary>The longest text of a name list in a variable (spec §5.4: «the first names and y N más»).</summary>
    public const int MaxNamesLength = 200;

    /// <summary>What the date variable carries when there is no lead (<c>upcomingDate</c> null): Meta rejects an empty value.</summary>
    public const string NoDate = "—";

    /// <summary>What the upcoming variable carries when there is no lead: the template still prints it after «Próximos (—):».</summary>
    public const string NoLead = "sin aviso anticipado";

    private const string Nobody = "nadie";

    private static readonly string[] Days = ["domingo", "lunes", "martes", "miércoles", "jueves", "viernes", "sábado"];

    private static readonly string[] Months =
    [
        "enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre",
    ];

    /// <summary>A list of <paramref name="names"/> (the ones shown) out of <paramref name="total"/> people, in at most <paramref name="maxLength"/>
    /// characters: «nadie» when there is nobody, the names joined with «, » when they all fit, otherwise the most names that fit followed by
    /// «y N más».</summary>
    public static string Names(IReadOnlyList<string> names, int total, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0)
        {
            return total > 0 ? Count(total) : Nobody;
        }

        var joined = string.Join(", ", names);
        if (total <= names.Count && joined.Length <= maxLength)
        {
            return joined;
        }

        for (var shown = names.Count; shown >= 1; shown--)
        {
            var remaining = total - shown;
            if (remaining <= 0)
            {
                continue;
            }

            var text = $"{string.Join(", ", names.Take(shown))} y {remaining.ToString(CultureInfo.InvariantCulture)} más";
            if (text.Length <= maxLength)
            {
                return text;
            }
        }

        // A single person whose name alone is too long: the name, cut to fit, is better than «1 persona».
        return total == 1 ? Truncate(names[0], maxLength) : Count(total);
    }

    /// <summary>The upcoming list of the template: <see cref="NoLead"/> when the setting has no lead (<paramref name="upcomingDate"/> null),
    /// otherwise <see cref="Names"/>.</summary>
    public static string Upcoming(IReadOnlyList<string> names, int total, DateOnly? upcomingDate, int maxLength) =>
        upcomingDate is null ? NoLead : Names(names, total, maxLength);

    /// <summary>«viernes 4 de diciembre», or <see cref="NoDate"/> when there is none. Written by hand: the host may run without ICU.</summary>
    public static string Date(DateOnly? date) =>
        date is { } value
            ? $"{Days[(int)value.DayOfWeek]} {value.Day.ToString(CultureInfo.InvariantCulture)} de {Months[value.Month - 1]}"
            : NoDate;

    private static string Count(int total) =>
        total == 1 ? "1 persona" : $"{total.ToString(CultureInfo.InvariantCulture)} personas";

    private static string Truncate(string name, int maxLength) =>
        name.Length <= maxLength ? name : maxLength <= 1 ? "…" : name[..(maxLength - 1)].TrimEnd() + "…";
}
