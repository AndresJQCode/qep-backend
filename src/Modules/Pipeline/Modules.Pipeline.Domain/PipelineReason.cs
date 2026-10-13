namespace Modules.Pipeline.Domain;

/// <summary>Un motivo de pérdida (D14) o de descarte (D29) del tenant. No se borra: el historial lo nombra (P8 del plan); se
/// desactiva.</summary>
public sealed class PipelineReason
{
    public const int NameMaxLength = 80;

    private PipelineReason() => Name = string.Empty;

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public ReasonKind Kind { get; private set; }

    public string Name { get; private set; }

    public int Position { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static PipelineReason Create(Guid tenantId, ReasonKind kind, string name, int position, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Kind = kind,
            Name = name,
            Position = position,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

    internal void Place(string name, int position, DateTimeOffset now)
    {
        Name = name;
        Position = position;
        IsActive = true;
        UpdatedAt = now;
    }

    internal void Deactivate(DateTimeOffset now)
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        UpdatedAt = now;
    }
}

/// <summary>Un motivo como lo pide el <c>PUT</c>: <see cref="Id"/> nulo es uno nuevo.</summary>
public sealed record ReasonInput(Guid? Id, string Name);

/// <summary>Reemplaza un catálogo entero (P8): renombra, reordena, agrega, reactiva lo que vuelve y desactiva lo que deja de
/// venir. Valida todo antes de tocar nada.</summary>
public static class ReasonCatalog
{
    public const int MaxActive = 30;

    /// <returns>Los motivos nuevos, para que el repositorio los agregue.</returns>
    public static IReadOnlyList<PipelineReason> Replace(
        Guid tenantId, ReasonKind kind, IReadOnlyCollection<PipelineReason> current, IReadOnlyList<ReasonInput> inputs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(inputs);
        var normalized = inputs.Select(input => input with { Name = (input.Name ?? string.Empty).Trim() }).ToArray();
        if (normalized.Length is 0 or > MaxActive)
        {
            throw Invalid($"A catalog has 1 to {MaxActive} reasons.");
        }

        if (normalized.Any(input => input.Name.Length is 0 or > PipelineReason.NameMaxLength))
        {
            throw Invalid($"Every reason needs a name of 1 to {PipelineReason.NameMaxLength} characters.");
        }

        if (normalized.Select(input => input.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
        {
            throw Invalid("Two reasons cannot share a name.");
        }

        var ids = normalized.Where(input => input.Id is not null).Select(input => input.Id!.Value).ToArray();
        if (ids.Distinct().Count() != ids.Length)
        {
            throw Invalid("A reason appears twice.");
        }

        var byId = current.Where(reason => reason.Kind == kind).ToDictionary(reason => reason.Id);
        if (ids.Any(id => !byId.ContainsKey(id)))
        {
            throw Invalid("A reason id does not belong to this catalog.");
        }

        var added = new List<PipelineReason>();
        for (var index = 0; index < normalized.Length; index++)
        {
            var input = normalized[index];
            var position = index + 1;
            if (input.Id is { } id)
            {
                byId[id].Place(input.Name, position, now);
            }
            else
            {
                added.Add(PipelineReason.Create(tenantId, kind, input.Name, position, now));
            }
        }

        var kept = ids.ToHashSet();
        foreach (var reason in byId.Values.Where(reason => !kept.Contains(reason.Id)))
        {
            reason.Deactivate(now);
        }

        return added;
    }

    private static PipelineDomainException Invalid(string message) => new(PipelineErrorCodes.ReasonLayoutInvalid, message);
}
