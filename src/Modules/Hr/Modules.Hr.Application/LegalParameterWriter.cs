using System.Globalization;
using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application;

/// <summary>Un valor escalar que el operador quiere dejar en <c>(Key, EffectiveFrom)</c>; <c>Value</c> en <c>null</c> borra la fila.</summary>
public sealed record LegalParameterEdit(LegalParameterKey Key, DateOnly EffectiveFrom, decimal? Value);

/// <summary>Un conjunto de tramos que el operador quiere dejar en <c>(Key, EffectiveFrom)</c>; <c>Brackets</c> en <c>null</c> borra el conjunto.</summary>
public sealed record LegalBracketSetEdit(LegalBracketKey Key, DateOnly EffectiveFrom, IReadOnlyList<LegalBracketInput>? Brackets);

/// <summary>De dónde viene un lote de cambios: lo manual no lleva lote ni archivo; Excel comparte un lote y el nombre del archivo.</summary>
public sealed record LegalParameterChangeOrigin(LegalParameterChangeSource Source, Guid? BatchId, string? FileName)
{
    public static readonly LegalParameterChangeOrigin Manual = new(LegalParameterChangeSource.Manual, null, null);
}

public sealed record LegalParameterWriteResult(int Created, int Updated, int Deleted, int Unchanged);

/// <summary>
/// 409 <c>hr.legal_parameter.in_use</c>: alguna nómina ya calculada se resolvió con una fecha dentro del rango que el cambio tocaría.
/// Sólo viajan conteos y rango (spec 2026-11-22, «Qué devuelve el 409»): ni empresas ni nóminas por nombre.
/// </summary>
public sealed class LegalParameterInUseException(LegalParameterUsageSummary usage)
    : ResourceConflictException(
        HrErrorCodes.LegalParameterInUse,
        string.Create(
            CultureInfo.InvariantCulture,
            $"Esta fila ya la usaron {usage.PeriodsAffected} período(s) de nómina calculados de {usage.CompaniesAffected} empresa(s), " +
            $"entre {usage.From:yyyy-MM-dd} y {usage.To:yyyy-MM-dd}. Un valor que ya usó una nómina no se corrige desde esta pantalla.")),
      IHasProblemExtensions
{
    public IReadOnlyDictionary<string, object?> ProblemExtensions { get; } = new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["periodsAffected"] = usage.PeriodsAffected,
        ["companiesAffected"] = usage.CompaniesAffected,
        ["from"] = usage.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["to"] = usage.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// La única puerta de escritura de los valores legales, compartida por el formulario y la carga por Excel: valida, rechaza lo que ya
/// usó una nómina calculada, escribe filas e historial en la <b>misma</b> unidad de trabajo y sube la versión de la caché sólo después
/// de confirmar. Todo o nada: nada se escribe hasta que cada edición del lote pasó todas las reglas.
/// </summary>
public sealed class LegalParameterWriter(
    ILegalParameterRepository repository,
    ILegalParameterUsage usage,
    IHrUnitOfWork unitOfWork,
    ILegalParameterCacheVersion cacheVersion,
    IExecutionContext executionContext,
    IHrMemberDirectory members,
    IClock clock)
{
    private const string NoName = "Sin nombre";

    private enum Operation
    {
        Create,
        Update,
        Delete,
    }

    // Una edición ya clasificada contra lo que hay en la base. `Row`/`Rows` son las filas existentes (nulas al crear).
    private sealed record ScalarPlan(LegalParameterEdit Edit, Operation Operation, LegalParameterValue? Row);

    private sealed record BracketPlan(LegalBracketSetEdit Edit, Operation Operation, IReadOnlyList<LegalParameterBracket> Rows);

    public async Task<LegalParameterWriteResult> ApplyAsync(
        Guid operatorTenantId,
        IReadOnlyList<LegalParameterEdit> parameters,
        IReadOnlyList<LegalBracketSetEdit> brackets,
        LegalParameterChangeOrigin origin,
        CancellationToken cancellationToken)
    {
        var values = await repository.ListValuesAsync(cancellationToken);
        var bracketRows = await repository.ListBracketsAsync(cancellationToken);

        EnsureNoRepeats(parameters, brackets);

        // Fase 1: validar y clasificar cada edición contra las filas existentes. Nada se escribe todavía.
        var unchanged = 0;
        var scalarPlans = new List<ScalarPlan>();
        foreach (var edit in parameters)
        {
            var row = values.FirstOrDefault(value => value.Key == edit.Key && value.EffectiveFrom == edit.EffectiveFrom);
            if (edit.Value is not { } value)
            {
                scalarPlans.Add(new ScalarPlan(edit, Operation.Delete, row ?? throw NotFound(LegalParameterLabels.Of(edit.Key).Name, edit.EffectiveFrom)));
                continue;
            }

            LegalParameterRules.EnsureValid(edit.Key, value);
            if (row is null)
            {
                scalarPlans.Add(new ScalarPlan(edit, Operation.Create, null));
            }
            else if (row.Value == value)
            {
                unchanged++;
            }
            else
            {
                scalarPlans.Add(new ScalarPlan(edit, Operation.Update, row));
            }
        }

        var bracketPlans = new List<BracketPlan>();
        foreach (var edit in brackets)
        {
            var rows = bracketRows
                .Where(row => row.Key == edit.Key && row.EffectiveFrom == edit.EffectiveFrom)
                .OrderBy(row => row.LowerBound)
                .ToList();
            if (edit.Brackets is not { } set)
            {
                if (rows.Count == 0)
                {
                    throw NotFound(LegalParameterLabels.NameOf(edit.Key), edit.EffectiveFrom);
                }

                bracketPlans.Add(new BracketPlan(edit, Operation.Delete, rows));
                continue;
            }

            LegalParameterRules.EnsureValidBrackets(set);
            if (rows.Count == 0)
            {
                bracketPlans.Add(new BracketPlan(edit, Operation.Create, rows));
            }
            else if (SameSet(rows, set))
            {
                unchanged++;
            }
            else
            {
                bracketPlans.Add(new BracketPlan(edit, Operation.Update, rows));
            }
        }

        if (scalarPlans.Count == 0 && bracketPlans.Count == 0)
        {
            return new LegalParameterWriteResult(0, 0, 0, unchanged);
        }

        // Fase 2: las fechas de cada clave tal como quedarán después de TODAS las ediciones del lote. Con ellas se decide si se borra
        // la última fila y dónde termina el rango de cada cambio (`N`, la siguiente vigencia).
        var scalarDates = values
            .GroupBy(value => value.Key)
            .ToDictionary(group => group.Key, group => group.Select(value => value.EffectiveFrom).ToHashSet());
        foreach (var plan in scalarPlans)
        {
            var dates = DatesOf(scalarDates, plan.Edit.Key);
            if (plan.Operation == Operation.Delete)
            {
                dates.Remove(plan.Edit.EffectiveFrom);
            }
            else
            {
                dates.Add(plan.Edit.EffectiveFrom);
            }
        }

        var bracketDates = bracketRows
            .GroupBy(row => row.Key)
            .ToDictionary(group => group.Key, group => group.Select(row => row.EffectiveFrom).ToHashSet());
        foreach (var plan in bracketPlans)
        {
            var dates = DatesOf(bracketDates, plan.Edit.Key);
            if (plan.Operation == Operation.Delete)
            {
                dates.Remove(plan.Edit.EffectiveFrom);
            }
            else
            {
                dates.Add(plan.Edit.EffectiveFrom);
            }
        }

        foreach (var plan in scalarPlans.Where(plan => plan.Operation == Operation.Delete))
        {
            if (DatesOf(scalarDates, plan.Edit.Key).Count == 0)
            {
                throw LastRow(LegalParameterLabels.Of(plan.Edit.Key).Name);
            }
        }

        foreach (var plan in bracketPlans.Where(plan => plan.Operation == Operation.Delete))
        {
            if (DatesOf(bracketDates, plan.Edit.Key).Count == 0)
            {
                throw LastRow(LegalParameterLabels.NameOf(plan.Edit.Key));
            }
        }

        // El uso se mira una sola vez, de todos los tenants, y sólo si hay algo que cambiar.
        var stamps = await usage.ListCalculatedPeriodsOfAllTenantsAsync(cancellationToken);
        foreach (var plan in scalarPlans)
        {
            EnsureNotInUse(plan.Edit.EffectiveFrom, DatesOf(scalarDates, plan.Edit.Key), stamps);
        }

        foreach (var plan in bracketPlans)
        {
            EnsureNotInUse(plan.Edit.EffectiveFrom, DatesOf(bracketDates, plan.Edit.Key), stamps);
        }

        // Fase 3: escribir filas e historial, en una sola unidad de trabajo.
        var subjectId = executionContext.SubjectId;
        var now = clock.UtcNow;
        var actor = await members.FindBySubjectAsync(operatorTenantId, subjectId, cancellationToken);
        var actorName = string.IsNullOrWhiteSpace(actor?.DisplayName) ? NoName : actor.DisplayName;

        LegalParameterChange Change(
            string key, DateOnly effectiveFrom, Operation operation, string? oldJson, string? newJson) =>
            LegalParameterChange.Record(
                key,
                effectiveFrom,
                operation switch
                {
                    Operation.Create => LegalParameterChangeAction.Created,
                    Operation.Update => LegalParameterChangeAction.Updated,
                    _ => LegalParameterChangeAction.Deleted,
                },
                oldJson,
                newJson,
                subjectId,
                actorName,
                now,
                origin.Source,
                origin.BatchId,
                origin.FileName);

        int created = 0, updated = 0, deleted = 0;
        foreach (var plan in scalarPlans)
        {
            var edit = plan.Edit;
            switch (plan.Operation)
            {
                case Operation.Create:
                    repository.Add(LegalParameterValue.Create(edit.Key, edit.EffectiveFrom, edit.Value!.Value, subjectId, now));
                    repository.AddChange(Change(
                        edit.Key.ToString(), edit.EffectiveFrom, plan.Operation, null, LegalParameterChangeJson.Number(edit.Value.Value)));
                    created++;
                    break;
                case Operation.Update:
                    var previous = plan.Row!.Value;
                    plan.Row.Replace(edit.Value!.Value, subjectId, now);
                    repository.AddChange(Change(
                        edit.Key.ToString(),
                        edit.EffectiveFrom,
                        plan.Operation,
                        LegalParameterChangeJson.Number(previous),
                        LegalParameterChangeJson.Number(edit.Value.Value)));
                    updated++;
                    break;
                default:
                    repository.Remove(plan.Row!);
                    repository.AddChange(Change(
                        edit.Key.ToString(), edit.EffectiveFrom, plan.Operation, LegalParameterChangeJson.Number(plan.Row!.Value), null));
                    deleted++;
                    break;
            }
        }

        foreach (var plan in bracketPlans)
        {
            var edit = plan.Edit;
            var oldJson = plan.Rows.Count == 0 ? null : LegalParameterChangeJson.Brackets(ToInputs(plan.Rows));
            var newJson = edit.Brackets is null ? null : LegalParameterChangeJson.Brackets(edit.Brackets);
            if (plan.Rows.Count > 0)
            {
                repository.RemoveBrackets(plan.Rows);
            }

            if (edit.Brackets is { } set)
            {
                repository.AddBrackets(set.Select(input =>
                    LegalParameterBracket.Create(edit.Key, edit.EffectiveFrom, input, subjectId, now)));
            }

            repository.AddChange(Change(edit.Key.ToString(), edit.EffectiveFrom, plan.Operation, oldJson, newJson));
            switch (plan.Operation)
            {
                case Operation.Create:
                    created++;
                    break;
                case Operation.Update:
                    updated++;
                    break;
                default:
                    deleted++;
                    break;
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Después del commit, y sin el token de quien llama: si el cliente cancela ahora, la versión igual se sube. No lanza.
        await cacheVersion.BumpAsync(CancellationToken.None);

        return new LegalParameterWriteResult(created, updated, deleted, unchanged);
    }

    private static void EnsureNoRepeats(
        IReadOnlyList<LegalParameterEdit> parameters, IReadOnlyList<LegalBracketSetEdit> brackets)
    {
        foreach (var repeated in parameters.GroupBy(edit => (edit.Key, edit.EffectiveFrom)).Where(group => group.Count() > 1))
        {
            throw Repeated(LegalParameterLabels.Of(repeated.Key.Key).Name, repeated.Key.EffectiveFrom);
        }

        foreach (var repeated in brackets.GroupBy(edit => (edit.Key, edit.EffectiveFrom)).Where(group => group.Count() > 1))
        {
            throw Repeated(LegalParameterLabels.NameOf(repeated.Key.Key), repeated.Key.EffectiveFrom);
        }
    }

    private static HashSet<DateOnly> DatesOf<TKey>(Dictionary<TKey, HashSet<DateOnly>> map, TKey key)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var dates))
        {
            dates = [];
            map[key] = dates;
        }

        return dates;
    }

    // `otherDates` incluye la fecha editada si la fila existe después del lote; la regla pide las otras.
    private static void EnsureNotInUse(
        DateOnly effectiveFrom, HashSet<DateOnly> finalDates, IReadOnlyCollection<CalculatedPeriodStamp> stamps)
    {
        var affected = LegalParameterUsageRule.Affected(
            effectiveFrom, finalDates.Where(date => date != effectiveFrom), stamps);
        if (affected is not null)
        {
            throw new LegalParameterInUseException(affected);
        }
    }

    private static bool SameSet(IReadOnlyList<LegalParameterBracket> rows, IReadOnlyList<LegalBracketInput> set) =>
        ToInputs(rows).SequenceEqual(set.OrderBy(input => input.LowerBound));

    private static List<LegalBracketInput> ToInputs(IEnumerable<LegalParameterBracket> rows) =>
        rows.OrderBy(row => row.LowerBound)
            .Select(row => new LegalBracketInput(row.LowerBound, row.UpperBound, row.Rate))
            .ToList();

    private static ResourceNotFoundException NotFound(string name, DateOnly effectiveFrom) =>
        new(HrErrorCodes.LegalParameterKeyUnknown, $"No hay una fila de {name} que rija desde {Format(effectiveFrom)}.");

    private static HrDomainException LastRow(string name) =>
        new(HrErrorCodes.LegalParameterLastRow, $"{name} necesita al menos una fila: no puedes borrar la única que tiene.");

    private static HrDomainException Repeated(string name, DateOnly effectiveFrom) =>
        new(
            HrErrorCodes.LegalParameterDateInvalid,
            $"{name} viene más de una vez con la fecha {Format(effectiveFrom)}: deja una sola.");

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
