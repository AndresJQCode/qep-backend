namespace Modules.Pipeline.Domain;

/// <summary>
/// Spec 2026-10-13 §4.1, D10–D12: el embudo del tenant. Uno por tenant en este slice, pero las etapas son hijas suyas (modelo
/// HubSpot), así que varios embudos después es quitar el índice único, no mover columnas. <see cref="ReplaceStages"/> recibe la
/// colección entera porque una etapa sola no sabe si es la única <c>Won</c>.
/// </summary>
public sealed class SalesPipeline
{
    public const int NameMaxLength = 80;
    public const int StageNameMaxLength = 60;
    public const int MaxStages = 20;
    public const int MaxStallDays = 365;
    public const string DefaultName = "Embudo de ventas";

    private readonly List<PipelineStage> _stages = [];

    private SalesPipeline() => Name = string.Empty;

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Sin orden: EF las carga como vengan. Para recorrerlas en orden, <see cref="OrderedStages"/>.</summary>
    public IReadOnlyCollection<PipelineStage> Stages => _stages;

    public IReadOnlyList<PipelineStage> OrderedStages => [.. _stages.OrderBy(stage => stage.Position)];

    public PipelineStage FirstOpenStage => OrderedStages.First(stage => stage.Kind == StageKind.Open);

    public PipelineStage WonStage => _stages.Single(stage => stage.Kind == StageKind.Won);

    public PipelineStage LostStage => _stages.Single(stage => stage.Kind == StageKind.Lost);

    public PipelineStage QuotationStage => _stages.Single(stage => stage.IsQuotationStage);

    public PipelineStage? FindStage(Guid stageId) => _stages.FirstOrDefault(stage => stage.Id == stageId);

    /// <summary>D31: el embudo con el que arranca todo tenant, editable después.</summary>
    public static SalesPipeline CreateDefault(Guid tenantId, DateTimeOffset now)
    {
        var pipeline = new SalesPipeline
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = DefaultName,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var position = 0;
        foreach (var seed in PipelineSeed.Stages)
        {
            position++;
            pipeline._stages.Add(PipelineStage.Create(
                pipeline.Id, seed.Name, position, seed.Probability, seed.Kind, seed.StallDays, seed.IsQuotationStage));
        }

        return pipeline;
    }

    /// <summary>
    /// D11 y D12: reemplaza la disposición entera. Valida todo antes de tocar nada; una etapa que estaba y ya no viene se borra,
    /// salvo que tenga negocios (<paramref name="stagesWithDeals"/>), y una etapa que existe no cambia de tipo (pasar una abierta
    /// a <c>Won</c> cerraría en silencio sus negocios).
    /// </summary>
    public void ReplaceStages(IReadOnlyList<StageInput> inputs, IReadOnlySet<Guid> stagesWithDeals, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(stagesWithDeals);
        var normalized = Validate(inputs);

        var kept = normalized.Where(input => input.Id is not null).Select(input => input.Id!.Value).ToHashSet();
        var removed = _stages.Where(stage => !kept.Contains(stage.Id)).ToArray();
        if (removed.Any(stage => stagesWithDeals.Contains(stage.Id)))
        {
            throw new PipelineDomainException(
                PipelineErrorCodes.StageHasDeals, "A stage with deals cannot be removed; move its deals first.");
        }

        foreach (var stage in removed)
        {
            _stages.Remove(stage);
        }

        for (var index = 0; index < normalized.Count; index++)
        {
            var input = normalized[index];
            var position = index + 1;
            if (input.Id is { } id)
            {
                _stages.Single(stage => stage.Id == id)
                    .Update(input.Name, position, input.Probability, input.StallDays, input.IsQuotationStage);
            }
            else
            {
                _stages.Add(PipelineStage.Create(
                    Id, input.Name, position, input.Probability, input.Kind, input.StallDays, input.IsQuotationStage));
            }
        }

        Version++;
        UpdatedAt = now;
    }

    private List<StageInput> Validate(IReadOnlyList<StageInput> inputs)
    {
        if (inputs.Count is 0 or > MaxStages)
        {
            throw Invalid($"A pipeline has 3 to {MaxStages} stages.");
        }

        var normalized = inputs.Select(input => input with { Name = (input.Name ?? string.Empty).Trim() }).ToList();
        if (normalized.Any(input => input.Name.Length is 0 or > StageNameMaxLength))
        {
            throw Invalid($"Every stage needs a name of 1 to {StageNameMaxLength} characters.");
        }

        if (normalized.Select(input => input.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Count)
        {
            throw Invalid("Two stages cannot share a name.");
        }

        var opens = normalized.TakeWhile(input => input.Kind == StageKind.Open).Count();
        if (opens == 0 || !normalized.Skip(opens).Select(input => input.Kind).SequenceEqual([StageKind.Won, StageKind.Lost]))
        {
            throw Invalid("Open stages go first, then exactly one Won stage and one Lost stage.");
        }

        if (normalized.Count(input => input.IsQuotationStage) != 1 ||
            normalized.Any(input => input.IsQuotationStage && input.Kind != StageKind.Open))
        {
            throw Invalid("Exactly one open stage is the quotation stage.");
        }

        foreach (var input in normalized)
        {
            if (input.Probability is < 0 or > 100)
            {
                throw Invalid("A probability goes from 0 to 100.");
            }

            var stallIsValid = input.Kind == StageKind.Open
                ? input.StallDays is >= 1 and <= MaxStallDays
                : input.StallDays is null;
            if (!stallIsValid)
            {
                throw Invalid($"Open stages need 1 to {MaxStallDays} stall days; closing stages have none.");
            }
        }

        var ids = normalized.Where(input => input.Id is not null).Select(input => input.Id!.Value).ToArray();
        if (ids.Distinct().Count() != ids.Length)
        {
            throw Invalid("A stage appears twice.");
        }

        foreach (var input in normalized.Where(input => input.Id is not null))
        {
            var current = FindStage(input.Id!.Value) ?? throw Invalid("A stage id does not belong to this pipeline.");
            if (current.Kind != input.Kind)
            {
                throw Invalid("A stage cannot change its kind.");
            }
        }

        return normalized;
    }

    private static PipelineDomainException Invalid(string message) => new(PipelineErrorCodes.StageLayoutInvalid, message);
}

/// <summary>Spec 2026-10-13 §4.1: una etapa del embudo. <see cref="Position"/> empieza en 1; <see cref="StallDays"/> sólo en las
/// abiertas.</summary>
public sealed class PipelineStage
{
    private PipelineStage() => Name = string.Empty;

    public Guid Id { get; private set; }

    public Guid PipelineId { get; private set; }

    public string Name { get; private set; }

    public int Position { get; private set; }

    public int Probability { get; private set; }

    public StageKind Kind { get; private set; }

    public int? StallDays { get; private set; }

    public bool IsQuotationStage { get; private set; }

    internal static PipelineStage Create(
        Guid pipelineId, string name, int position, int probability, StageKind kind, int? stallDays, bool isQuotationStage) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            PipelineId = pipelineId,
            Name = name,
            Position = position,
            Probability = probability,
            Kind = kind,
            StallDays = stallDays,
            IsQuotationStage = isQuotationStage,
        };

    internal void Update(string name, int position, int probability, int? stallDays, bool isQuotationStage)
    {
        Name = name;
        Position = position;
        Probability = probability;
        StallDays = stallDays;
        IsQuotationStage = isQuotationStage;
    }
}

/// <summary>Una etapa como la pide el <c>PUT /settings/stages</c>: <see cref="Id"/> nulo es una etapa nueva.</summary>
public sealed record StageInput(Guid? Id, string Name, StageKind Kind, int Probability, int? StallDays, bool IsQuotationStage);

/// <summary>Una etapa de la semilla (D31).</summary>
public sealed record SeedStage(string Name, StageKind Kind, int Probability, int? StallDays, bool IsQuotationStage);

/// <summary>Spec 2026-10-13 D31: con lo que arranca todo tenant. Editable después desde Ajustes.</summary>
public static class PipelineSeed
{
    public static IReadOnlyList<SeedStage> Stages { get; } =
    [
        new("Nuevo", StageKind.Open, 10, 3, false),
        new("Calificado", StageKind.Open, 25, 5, false),
        new("Cotizado", StageKind.Open, 50, 7, true),
        new("Negociación", StageKind.Open, 75, 7, false),
        new("Ganado", StageKind.Won, 100, null, false),
        new("Perdido", StageKind.Lost, 0, null, false),
    ];

    public static IReadOnlyList<string> LossReasons { get; } = ["Precio", "Competencia", "Sin respuesta", "Sin presupuesto", "Otro"];

    public static IReadOnlyList<string> DismissReasons { get; } = ["Soporte", "Recompra", "Proveedor", "Spam", "Otro"];
}
