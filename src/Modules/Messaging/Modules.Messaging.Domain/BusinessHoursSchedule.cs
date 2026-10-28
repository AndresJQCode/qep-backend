namespace Modules.Messaging.Domain;

/// <summary>
/// Spec 2026-10-21 §5.2: un horario de atención del tenant. La semana son siempre siete días (1 = lunes … 7 = domingo); sin franjas,
/// ese día no se atiende. Para una fecha local manda, en orden: la excepción de esa fecha, el festivo del país (cierra todo el día)
/// y el día de la semana. El agregado guarda listas: <c>jsonb</c> es asunto de Infrastructure. La unicidad del nombre y del
/// predeterminado las arbitran los índices, no esta clase.
/// </summary>
public sealed class BusinessHoursSchedule
{
    public const int NameMaxLength = 60;
    public const int MaxRangesPerDay = 3;
    public const int MaxExceptions = 100;
    public const int MinutesPerDay = 1440;

    /// <summary>Hasta dónde busca <see cref="NextChangeAt"/>: pasado este plazo responde <c>null</c>.</summary>
    public static readonly TimeSpan NextChangeHorizon = TimeSpan.FromDays(14);

    // EF Core materializa por acá; Create es el único punto de entrada.
    private BusinessHoursSchedule()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public bool IsDefault { get; private set; }

    /// <summary>Siempre siete entradas, ordenadas del 1 al 7.</summary>
    public IReadOnlyList<WeekdayHours> Weekly { get; private set; } = [];

    /// <summary>Ordenadas por fecha; las pasadas se descartan al guardar.</summary>
    public IReadOnlyList<DateException> Exceptions { get; private set; } = [];

    public HolidayCountry? HolidayCountry { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    public static BusinessHoursSchedule Create(
        Guid id,
        Guid tenantId,
        string? name,
        IReadOnlyList<WeekdayHours> weekly,
        IReadOnlyList<DateException> exceptions,
        HolidayCountry? holidayCountry,
        DateOnly today,
        DateTimeOffset now) => new()
        {
            HolidayCountry = NormalizeCountry(holidayCountry),
            Id = id,
            TenantId = tenantId,
            Name = NormalizeName(name),
            Weekly = NormalizeWeekly(weekly),
            Exceptions = NormalizeExceptions(exceptions, today),
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };

    /// <summary>Valida todo antes de asignar: un rechazo deja el horario como estaba.</summary>
    public void Update(
        string? name,
        IReadOnlyList<WeekdayHours> weekly,
        IReadOnlyList<DateException> exceptions,
        HolidayCountry? holidayCountry,
        DateOnly today,
        DateTimeOffset now)
    {
        var normalizedName = NormalizeName(name);
        var normalizedWeekly = NormalizeWeekly(weekly);
        var normalizedExceptions = NormalizeExceptions(exceptions, today);
        var normalizedCountry = NormalizeCountry(holidayCountry);

        Name = normalizedName;
        Weekly = normalizedWeekly;
        Exceptions = normalizedExceptions;
        HolidayCountry = normalizedCountry;
        UpdatedAt = now;
        Version++;
    }

    public void MakeDefault(DateTimeOffset now)
    {
        IsDefault = true;
        UpdatedAt = now;
        Version++;
    }

    public void ClearDefault(DateTimeOffset now)
    {
        IsDefault = false;
        UpdatedAt = now;
        Version++;
    }

    /// <summary>Misma semana, excepciones y país; nunca predeterminado. El nombre lo elige quien llama con <see cref="DuplicateName"/>.</summary>
    public BusinessHoursSchedule Duplicate(Guid newId, string name, DateTimeOffset now) => new()
    {
        Id = newId,
        TenantId = TenantId,
        Name = NormalizeName(name),
        Weekly = Weekly,
        Exceptions = Exceptions,
        HolidayCountry = HolidayCountry,
        CreatedAt = now,
        UpdatedAt = now,
        Version = 1,
    };

    public bool IsOpenAt(DateTimeOffset instant, TimeZoneInfo timeZone)
    {
        var local = TimeZoneInfo.ConvertTime(instant, timeZone).DateTime;
        var minute = (int)local.TimeOfDay.TotalMinutes;
        return EffectiveRanges(DateOnly.FromDateTime(local)).Any(range => minute >= range.From && minute < range.To);
    }

    /// <summary>
    /// El próximo instante en que cambia abierto/cerrado, hasta <see cref="NextChangeHorizon"/>; <c>null</c> si no cambia. No barre
    /// minuto a minuto: recorre los días locales y mira los bordes de las franjas efectivas de cada uno.
    /// </summary>
    public DateTimeOffset? NextChangeAt(DateTimeOffset instant, TimeZoneInfo timeZone)
    {
        var wasOpen = IsOpenAt(instant, timeZone);
        var limit = instant + NextChangeHorizon;
        var firstDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, timeZone).DateTime);

        // Un día más allá de los 14 por si el límite cae a media jornada; los candidatos se acotan abajo.
        for (var offset = 0; offset <= 15; offset++)
        {
            var date = firstDay.AddDays(offset);
            var candidates = EffectiveRanges(date)
                .SelectMany(range => new[] { range.From, range.To })
                .Distinct()
                .Select(minute => LocalToUtc(date, minute, timeZone))
                .Where(candidate => candidate > instant && candidate <= limit)
                .OrderBy(candidate => candidate);

            foreach (var candidate in candidates)
            {
                if (IsOpenAt(candidate, timeZone) != wasOpen)
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>Los festivos que la página muestra, para que el frontend no tenga la regla. Vacío sin país.</summary>
    public IReadOnlyList<DateOnly> UpcomingHolidays(DateOnly fromLocal, int months = 12)
    {
        if (HolidayCountry != Domain.HolidayCountry.CO)
        {
            return [];
        }

        var until = fromLocal.AddMonths(months);
        return Enumerable.Range(fromLocal.Year, until.Year - fromLocal.Year + 1)
            .SelectMany(year => ColombianHolidays.For(year))
            .Where(date => date >= fromLocal && date < until)
            .OrderBy(date => date)
            .ToList();
    }

    /// <summary>D-H14: intento 1 → «{name} (copia)»; n ≥ 2 → «{name} (copia n)». Se recorta el nombre, no el sufijo, a 60.</summary>
    public static string DuplicateName(string name, int attempt)
    {
        var suffix = attempt <= 1 ? " (copia)" : $" (copia {attempt})";
        var trimmed = name.Trim();
        var room = NameMaxLength - suffix.Length;
        return (trimmed.Length <= room ? trimmed : trimmed[..room].TrimEnd()) + suffix;
    }

    private IReadOnlyList<TimeRange> EffectiveRanges(DateOnly date)
    {
        var exception = Exceptions.FirstOrDefault(e => e.Date == date);
        if (exception is not null)
        {
            return exception.Ranges;
        }

        if (HolidayCountry == Domain.HolidayCountry.CO && ColombianHolidays.For(date.Year).Contains(date))
        {
            return [];
        }

        var day = date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek;
        return Weekly[day - 1].Ranges;
    }

    /// <summary>
    /// Minuto local → instante. En un hueco de horario de verano (hora inexistente) se toma el primer instante válido posterior al
    /// hueco; en una hora ambigua (se repite al retroceder el reloj) se toma la primera aparición, la del offset mayor.
    /// </summary>
    private static DateTimeOffset LocalToUtc(DateOnly date, int minute, TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue).AddMinutes(minute), DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        var offset = timeZone.IsAmbiguousTime(local)
            ? timeZone.GetAmbiguousTimeOffsets(local).Max()
            : timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private static string NormalizeName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > NameMaxLength)
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.BusinessHoursNameInvalid,
                $"El nombre del horario debe tener entre 1 y {NameMaxLength} caracteres.");
        }

        return trimmed;
    }

    private static List<WeekdayHours> NormalizeWeekly(IReadOnlyList<WeekdayHours> weekly)
    {
        if (weekly.Count != 7 || weekly.Select(w => w.Day).Distinct().Count() != 7 || weekly.Any(w => w.Day is < 1 or > 7))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.BusinessHoursRangesInvalid,
                "La semana debe traer los siete días, del 1 (lunes) al 7 (domingo), sin repetir.");
        }

        var ordered = weekly.OrderBy(w => w.Day).ToList();
        foreach (var day in ordered)
        {
            if (day.Ranges is null || !AreValidRanges(day.Ranges))
            {
                throw new MessagingDomainException(
                    MessagingErrorCodes.BusinessHoursRangesInvalid,
                    $"Las franjas del día {day.Day} no son válidas: máximo {MaxRangesPerDay}, ordenadas, sin solaparse y entre 0 y {MinutesPerDay} minutos.");
            }
        }

        // Copia de las franjas: el agregado es dueño de su estado.
        return ordered.Select(day => day with { Ranges = [.. day.Ranges] }).ToList();
    }

    private static HolidayCountry? NormalizeCountry(HolidayCountry? holidayCountry) =>
        holidayCountry is { } country && !Enum.IsDefined(country)
            ? throw new MessagingDomainException(MessagingErrorCodes.BusinessHoursHolidayCountryInvalid, "Sólo están disponibles los festivos de países soportados.")
            : holidayCountry;

    private static List<DateException> NormalizeExceptions(IReadOnlyList<DateException> exceptions, DateOnly today)
    {
        if (exceptions.Select(e => e.Date).Distinct().Count() != exceptions.Count)
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.BusinessHoursExceptionsInvalid,
                "No puede haber dos excepciones para la misma fecha.");
        }

        // D-H15: las pasadas se descartan antes del tope, para que un horario viejo siga siendo editable.
        var current = exceptions.Where(e => e.Date >= today).OrderBy(e => e.Date).ToList();
        if (current.Count > MaxExceptions)
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.BusinessHoursExceptionsInvalid,
                $"Un horario admite hasta {MaxExceptions} excepciones vigentes.");
        }

        if (current.Any(e => e.Ranges is null || !AreValidRanges(e.Ranges)))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.BusinessHoursExceptionsInvalid,
                $"Las franjas de una excepción no son válidas: máximo {MaxRangesPerDay}, ordenadas, sin solaparse y entre 0 y {MinutesPerDay} minutos.");
        }

        return current.Select(e => e with { Ranges = [.. e.Ranges] }).ToList();
    }

    private static bool AreValidRanges(IReadOnlyList<TimeRange> ranges)
    {
        if (ranges.Count > MaxRangesPerDay)
        {
            return false;
        }

        for (var i = 0; i < ranges.Count; i++)
        {
            var range = ranges[i];
            if (range.From < 0 || range.To > MinutesPerDay || range.From >= range.To)
            {
                return false;
            }

            if (i > 0 && ranges[i - 1].To > range.From)
            {
                return false;
            }
        }

        return true;
    }
}
