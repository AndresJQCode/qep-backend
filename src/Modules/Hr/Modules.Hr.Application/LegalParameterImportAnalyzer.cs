using System.Globalization;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Hr.Application;

/// <summary>
/// Lo que la vista previa y la confirmación ven del archivo: la vista previa que se le muestra al operador y, ya resueltas, las
/// ediciones que el escritor aplicaría (vacías si hay algún error). <c>Carried</c> son las filas que sólo repiten el valor vigente.
/// </summary>
public sealed record LegalParameterImportAnalysis(
    string FileName,
    LegalParameterImportPreviewDto Preview,
    IReadOnlyList<LegalParameterEdit> ParameterEdits,
    IReadOnlyList<LegalBracketSetEdit> BracketEdits,
    int Carried);

/// <summary>
/// El camino compartido de la vista previa y la confirmación de la carga anual de valores legales (spec 2026-11-22, «Carga por
/// Excel»): archivo → filas → reglas de fila → clasificación contra lo que hay → uso por nóminas calculadas → avisos. No escribe.
/// Como no hay staging, la confirmación vuelve a correr todo sobre el mismo archivo.
/// </summary>
public sealed class LegalParameterImportAnalyzer(
    IExcelLegalParameterImporter importer,
    ILegalParameterRepository repository,
    ILegalParameterUsage usage,
    ITenantClock tenantClock)
{
    private const string ParameterKind = "Parameter";
    private const string BracketsKind = "Brackets";
    private const int MaxKeyTextLength = 40;

    // Un elemento del análisis: el DTO que se muestra y, mientras no tenga error, la edición que lo aplica.
    private sealed class Entry(
        LegalParameterImportItemDto item, LegalParameterEdit? parameterEdit = null, LegalBracketSetEdit? bracketEdit = null)
    {
        public LegalParameterImportItemDto Item { get; set; } = item;

        public LegalParameterEdit? ParameterEdit { get; set; } = parameterEdit;

        public LegalBracketSetEdit? BracketEdit { get; set; } = bracketEdit;

        public bool IsError => Item.Action == LegalParameterImportActions.Error;

        public bool IsWritten => Item.Action is LegalParameterImportActions.Create
            or LegalParameterImportActions.Carry
            or LegalParameterImportActions.Update;

        public void Fail(string code, string message)
        {
            Item = Item with { Action = LegalParameterImportActions.Error, Code = code, Message = message };
            ParameterEdit = null;
            BracketEdit = null;
        }
    }

    public async Task<LegalParameterImportAnalysis> AnalyzeAsync(
        TenantId operatorTenantId, string? fileName, long sizeInBytes, Stream content, CancellationToken cancellationToken)
    {
        var name = EnsureAcceptableFile(fileName, sizeInBytes);
        var workbook = await importer.ReadAsync(content, cancellationToken);

        var totalRows = workbook.Parameters.Count + workbook.Brackets.Count;
        if (totalRows == 0)
        {
            throw new HrDomainException(HrErrorCodes.LegalParameterImportEmpty, "El archivo no tiene filas con datos.");
        }

        if (totalRows > LegalParameterImportColumns.MaxRows)
        {
            throw new HrDomainException(
                HrErrorCodes.LegalParameterImportTooManyRows,
                $"El archivo tiene más de {LegalParameterImportColumns.MaxRows} filas entre las dos hojas.");
        }

        var today = (await tenantClock.GetAsync(operatorTenantId.Value, cancellationToken)).Today;
        var values = await repository.ListValuesAsync(cancellationToken);
        var bracketRows = await repository.ListBracketsAsync(cancellationToken);

        var entries = new List<Entry>();
        entries.AddRange(AnalyzeParameters(workbook.Parameters, values));
        entries.AddRange(AnalyzeBrackets(workbook.Brackets, bracketRows));

        await RejectUsedRangesAsync(entries, values, bracketRows, cancellationToken);

        var items = entries.Select(entry => entry.Item).ToList();
        var summary = new LegalParameterImportSummaryDto(
            Count(items, LegalParameterImportActions.Create),
            Count(items, LegalParameterImportActions.Carry),
            Count(items, LegalParameterImportActions.Update),
            Count(items, LegalParameterImportActions.Unchanged),
            Count(items, LegalParameterImportActions.Error));
        var preview = new LegalParameterImportPreviewDto(
            name, summary, summary.Error == 0, items, MissingNextYear(entries, values, bracketRows, today.Year + 1));

        return new LegalParameterImportAnalysis(
            name,
            preview,
            entries.Where(entry => entry.ParameterEdit is not null).Select(entry => entry.ParameterEdit!).ToList(),
            entries.Where(entry => entry.BracketEdit is not null).Select(entry => entry.BracketEdit!).ToList(),
            summary.Carry);
    }

    private static int Count(IEnumerable<LegalParameterImportItemDto> items, string action) =>
        items.Count(item => item.Action == action);

    private static List<Entry> AnalyzeParameters(
        IReadOnlyList<ExcelLegalParameterRow> rows, IReadOnlyList<LegalParameterValue> values)
    {
        var entries = new List<Entry>();
        var seen = new HashSet<(LegalParameterKey Key, DateOnly Date)>();
        foreach (var row in rows)
        {
            // Una fila sin «Valor nuevo» es una fila de la plantilla que el operador no quiso tocar (las claves que no son anuales
            // vienen así): se ignora entera, sin validar su clave ni su fecha.
            if (row.NewValue is null)
            {
                continue;
            }

            entries.Add(AnalyzeParameter(row, values, seen));
        }

        return entries;
    }

    private static Entry AnalyzeParameter(
        ExcelLegalParameterRow row, IReadOnlyList<LegalParameterValue> values, HashSet<(LegalParameterKey Key, DateOnly Date)> seen)
    {
        LegalParameterImportItemDto Error(
            string code, string message, LegalParameterKey? key = null, DateOnly? date = null, decimal? newValue = null) =>
            new(
                ParameterKind,
                LegalParameterImportColumns.ParametersSheet,
                [row.RowNumber],
                key?.ToString() ?? row.Key,
                key is { } known ? LegalParameterLabels.Of(known).Name : null,
                date,
                LegalParameterImportActions.Error,
                null,
                newValue,
                null,
                null,
                code,
                message);

        if (!TryParseKey(row.Key, out var key))
        {
            return new Entry(Error(
                HrErrorCodes.LegalParameterImportRow.KeyUnknown, UnknownKeyMessage(row.Key)));
        }

        var label = LegalParameterLabels.Of(key);
        if (!TryParseDate(row.EffectiveFrom, out var date))
        {
            return new Entry(Error(HrErrorCodes.LegalParameterImportRow.DateInvalid, DateMessage(label.Name), key));
        }

        if (!seen.Add((key, date)))
        {
            return new Entry(Error(
                HrErrorCodes.LegalParameterImportRow.Duplicated,
                $"{label.Name} viene más de una vez con la fecha {Format(date)}: deja una sola.",
                key,
                date));
        }

        if (!TryParseNumber(row.NewValue, out var value))
        {
            return new Entry(Error(
                HrErrorCodes.LegalParameterImportRow.ValueInvalid,
                $"El valor de {label.Name} no es un número: revisa la celda «Valor nuevo».",
                key,
                date));
        }

        if (LegalParameterRules.Check(key, value) is { } message)
        {
            return new Entry(Error(HrErrorCodes.LegalParameterImportRow.ValueInvalid, message, key, date, value));
        }

        var existing = values.FirstOrDefault(item => item.Key == key && item.EffectiveFrom == date);
        var inForce = existing ?? values
            .Where(item => item.Key == key && item.EffectiveFrom <= date)
            .MaxBy(item => item.EffectiveFrom);
        var action = existing is not null
            ? existing.Value == value ? LegalParameterImportActions.Unchanged : LegalParameterImportActions.Update
            : inForce?.Value == value ? LegalParameterImportActions.Carry : LegalParameterImportActions.Create;

        var item = new LegalParameterImportItemDto(
            ParameterKind,
            LegalParameterImportColumns.ParametersSheet,
            [row.RowNumber],
            key.ToString(),
            label.Name,
            date,
            action,
            inForce?.Value,
            value,
            null,
            null,
            null,
            null);
        return new Entry(
            item, action == LegalParameterImportActions.Unchanged ? null : new LegalParameterEdit(key, date, value));
    }

    private static List<Entry> AnalyzeBrackets(
        IReadOnlyList<ExcelLegalBracketRow> rows, IReadOnlyList<LegalParameterBracket> bracketRows)
    {
        // Un conjunto es todas sus filas de la hoja con la misma clave y fecha, estén seguidas o no.
        var groups = new List<(string? Key, string? Date, List<ExcelLegalBracketRow> Rows)>();
        foreach (var row in rows)
        {
            var group = groups.FindIndex(item => item.Key == row.Key && item.Date == row.EffectiveFrom);
            if (group < 0)
            {
                groups.Add((row.Key, row.EffectiveFrom, [row]));
            }
            else
            {
                groups[group].Rows.Add(row);
            }
        }

        return groups.Select(group => AnalyzeSet(group.Key, group.Date, group.Rows, bracketRows)).ToList();
    }

    private static Entry AnalyzeSet(
        string? keyText, string? dateText, List<ExcelLegalBracketRow> rows, IReadOnlyList<LegalParameterBracket> bracketRows)
    {
        var rowNumbers = rows.Select(row => row.RowNumber).ToList();

        LegalParameterImportItemDto Error(
            string code, string message, LegalBracketKey? key = null, DateOnly? date = null) =>
            new(
                BracketsKind,
                LegalParameterImportColumns.BracketsSheet,
                rowNumbers,
                key?.ToString() ?? keyText,
                key is { } known ? LegalParameterLabels.NameOf(known) : null,
                date,
                LegalParameterImportActions.Error,
                null,
                null,
                null,
                null,
                code,
                message);

        if (!TryParseBracketKey(keyText, out var key))
        {
            return new Entry(Error(HrErrorCodes.LegalParameterImportRow.KeyUnknown, UnknownKeyMessage(keyText)));
        }

        var name = LegalParameterLabels.NameOf(key);
        if (!TryParseDate(dateText, out var date))
        {
            return new Entry(Error(HrErrorCodes.LegalParameterImportRow.DateInvalid, DateMessage(name), key));
        }

        var inputs = new List<LegalBracketInput>();
        foreach (var row in rows)
        {
            decimal upperValue = 0m;
            if (!TryParseNumber(row.LowerBound, out var lower)
                || !TryParseNumber(row.Rate, out var rate)
                || (row.UpperBound is not null && !TryParseNumber(row.UpperBound, out upperValue)))
            {
                return new Entry(Error(
                    HrErrorCodes.LegalParameterImportRow.BracketsInvalid,
                    $"El tramo de la fila {row.RowNumber.ToString(CultureInfo.InvariantCulture)} tiene una celda que no es un número: " +
                    "revisa «Desde», «Hasta» y «Tasa».",
                    key,
                    date));
            }

            inputs.Add(new LegalBracketInput(lower, row.UpperBound is null ? null : upperValue, rate));
        }

        // Las filas pueden venir en cualquier orden: el conjunto se ordena por límite inferior antes de validarse.
        var sorted = inputs.OrderBy(input => input.LowerBound).ToList();
        if (LegalParameterRules.CheckBrackets(sorted) is { } message)
        {
            return new Entry(Error(HrErrorCodes.LegalParameterImportRow.BracketsInvalid, message, key, date));
        }

        var existing = Set(bracketRows, key, date, exact: true);
        var inForce = existing ?? Set(bracketRows, key, date, exact: false);
        var action = existing is not null
            ? existing.SequenceEqual(sorted) ? LegalParameterImportActions.Unchanged : LegalParameterImportActions.Update
            : inForce is not null && inForce.SequenceEqual(sorted) ? LegalParameterImportActions.Carry : LegalParameterImportActions.Create;

        var item = new LegalParameterImportItemDto(
            BracketsKind,
            LegalParameterImportColumns.BracketsSheet,
            rowNumbers,
            key.ToString(),
            name,
            date,
            action,
            null,
            null,
            inForce?.Select(ToDto).ToList(),
            sorted.Select(ToDto).ToList(),
            null,
            null);
        return new Entry(
            item,
            bracketEdit: action == LegalParameterImportActions.Unchanged ? null : new LegalBracketSetEdit(key, date, sorted));
    }

    // El conjunto que rige en `date` (el último con fecha menor o igual), o sólo el de esa fecha exacta.
    private static List<LegalBracketInput>? Set(
        IReadOnlyList<LegalParameterBracket> bracketRows, LegalBracketKey key, DateOnly date, bool exact)
    {
        var candidates = bracketRows.Where(row => row.Key == key && (exact ? row.EffectiveFrom == date : row.EffectiveFrom <= date)).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var effective = candidates.Max(row => row.EffectiveFrom);
        return candidates
            .Where(row => row.EffectiveFrom == effective)
            .OrderBy(row => row.LowerBound)
            .Select(row => new LegalBracketInput(row.LowerBound, row.UpperBound, row.Rate))
            .ToList();
    }

    // Lo que ya usó una nómina calculada no se toca. El rango de cada fila termina en la siguiente fecha de su clave, existente o
    // traída por el mismo archivo: así se calcula igual que en el escritor.
    private async Task RejectUsedRangesAsync(
        List<Entry> entries,
        IReadOnlyList<LegalParameterValue> values,
        IReadOnlyList<LegalParameterBracket> bracketRows,
        CancellationToken cancellationToken)
    {
        var written = entries.Where(entry => entry.IsWritten).ToList();
        if (written.Count == 0)
        {
            return;
        }

        var stamps = await usage.ListCalculatedPeriodsOfAllTenantsAsync(cancellationToken);
        var fileDates = written
            .GroupBy(entry => (entry.Item.Kind, entry.Item.Key))
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.Item.EffectiveFrom!.Value).ToHashSet());

        foreach (var entry in written)
        {
            var item = entry.Item;
            var date = item.EffectiveFrom!.Value;
            var dates = new HashSet<DateOnly>(fileDates[(item.Kind, item.Key)]);
            if (item.Kind == ParameterKind)
            {
                dates.UnionWith(values.Where(value => value.Key.ToString() == item.Key).Select(value => value.EffectiveFrom));
            }
            else
            {
                dates.UnionWith(bracketRows.Where(row => row.Key.ToString() == item.Key).Select(row => row.EffectiveFrom));
            }

            var affected = LegalParameterUsageRule.Affected(date, dates.Where(other => other != date), stamps);
            if (affected is not null)
            {
                entry.Fail(HrErrorCodes.LegalParameterImportRow.InUse, new LegalParameterInUseException(affected).Message);
            }
        }
    }

    // Las 27 claves anuales y el conjunto a las que, después de aplicar el archivo, todavía les falta la fila del 1 de enero siguiente.
    private static List<LegalParameterImportWarningDto> MissingNextYear(
        List<Entry> entries,
        IReadOnlyList<LegalParameterValue> values,
        IReadOnlyList<LegalParameterBracket> bracketRows,
        int nextYear)
    {
        var applied = entries.Where(entry => entry.IsWritten).ToList();
        var warnings = new List<LegalParameterImportWarningDto>();

        foreach (var key in LegalParameterLabels.YearlyKeys)
        {
            var dates = values.Where(value => value.Key == key).Select(value => value.EffectiveFrom)
                .Concat(applied.Where(entry => entry.Item.Kind == ParameterKind && entry.Item.Key == key.ToString())
                    .Select(entry => entry.Item.EffectiveFrom!.Value));
            if (LegalParameterYears.MissingFor(dates, nextYear))
            {
                warnings.Add(Warning(key.ToString(), LegalParameterLabels.Of(key).Name, nextYear));
            }
        }

        foreach (var key in Enum.GetValues<LegalBracketKey>())
        {
            var dates = bracketRows.Where(row => row.Key == key).Select(row => row.EffectiveFrom)
                .Concat(applied.Where(entry => entry.Item.Kind == BracketsKind && entry.Item.Key == key.ToString())
                    .Select(entry => entry.Item.EffectiveFrom!.Value));
            if (LegalParameterYears.MissingFor(dates, nextYear))
            {
                warnings.Add(Warning(key.ToString(), LegalParameterLabels.NameOf(key), nextYear));
            }
        }

        return warnings;
    }

    private static LegalParameterImportWarningDto Warning(string key, string name, int nextYear) =>
        new(
            HrErrorCodes.LegalParameterImportNextYearMissing,
            key,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{name} se queda sin fila para {nextYear}: después de esta carga nada rige desde {nextYear}-01-01."));

    private static string EnsureAcceptableFile(string? fileName, long sizeInBytes)
    {
        if (string.IsNullOrWhiteSpace(fileName) || sizeInBytes <= 0)
        {
            throw new HrDomainException(HrErrorCodes.LegalParameterImportFileRequired, "Adjunta un archivo .xlsx.");
        }

        if (!fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new HrDomainException(HrErrorCodes.LegalParameterImportExtensionInvalid, "Sólo se aceptan archivos .xlsx.");
        }

        if (sizeInBytes > LegalParameterImportColumns.MaxSizeInBytes)
        {
            throw new HrDomainException(
                HrErrorCodes.LegalParameterImportTooLarge,
                $"El archivo pasa de {LegalParameterImportColumns.MaxSizeInBytes / (1024 * 1024)} MB.");
        }

        return fileName.Trim();
    }

    // El nombre exacto del enum: `smmlv` o un número no son claves, como en la ruta.
    private static bool TryParseKey(string? text, out LegalParameterKey key)
    {
        key = default;
        if (text is null || !Enum.GetNames<LegalParameterKey>().Contains(text, StringComparer.Ordinal))
        {
            return false;
        }

        key = Enum.Parse<LegalParameterKey>(text);
        return true;
    }

    private static bool TryParseBracketKey(string? text, out LegalBracketKey key)
    {
        key = default;
        if (text is null || !Enum.GetNames<LegalBracketKey>().Contains(text, StringComparer.Ordinal))
        {
            return false;
        }

        key = Enum.Parse<LegalBracketKey>(text);
        return true;
    }

    private static bool TryParseDate(string? text, out DateOnly date)
    {
        date = default;
        return text is not null
            && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    // El lector entrega los números con punto decimal; una coma o un separador de miles no es un número válido.
    private static bool TryParseNumber(string? text, out decimal value)
    {
        value = default;
        return text is not null
            && decimal.TryParse(
                text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    private static LegalBracketDto ToDto(LegalBracketInput input) => new(input.LowerBound, input.UpperBound, input.Rate);

    private static string UnknownKeyMessage(string? key) =>
        key is null
            ? "Falta la clave: usa el nombre exacto de la columna «Clave» de la plantilla."
            : $"La clave «{(key.Length <= MaxKeyTextLength ? key : key[..MaxKeyTextLength] + "…")}» no existe: usa el nombre exacto de la plantilla.";

    private static string DateMessage(string name) =>
        $"La fecha de «Rige desde» de {name} no es válida: usa el formato aaaa-mm-dd.";

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
