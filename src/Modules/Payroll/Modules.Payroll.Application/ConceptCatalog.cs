using Modules.Payroll.Domain;

namespace Modules.Payroll.Application;

/// <summary>Un concepto resuelto, del sistema o propio de la empresa: lo que necesitan capturas, importación y liquidación.</summary>
public sealed record ResolvedConcept(
    Guid Id,
    ConceptKind Kind,
    string Name,
    bool System,
    SystemConceptKey? SystemKey,
    bool? DefaultSalaryConstitutive,
    decimal? Multiplier,
    bool Archived);

/// <summary>El catálogo de una empresa en un momento: los doce del sistema (R-11) más los propios, archivados incluidos.</summary>
public sealed class ConceptCatalogSnapshot
{
    private readonly IReadOnlyDictionary<Guid, ResolvedConcept> _byId;
    private readonly IReadOnlyDictionary<string, ResolvedConcept> _activeByFoldedName;
    private readonly IReadOnlyList<PayrollConceptDto> _dtos;
    private readonly IReadOnlyDictionary<Guid, DateTimeOffset> _archivedAt;

    internal ConceptCatalogSnapshot(IReadOnlyList<PayrollConcept> own)
    {
        var system = SystemConcepts.All;
        var ordered = own
            .OrderBy(concept => concept.Kind)
            .ThenBy(concept => concept.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(concept => concept.Id.Value)
            .ToList();

        var byId = new Dictionary<Guid, ResolvedConcept>();
        var dtos = new List<PayrollConceptDto>(system.Count + ordered.Count);
        var archivedAt = new Dictionary<Guid, DateTimeOffset>();
        foreach (var concept in system)
        {
            byId[concept.Id] = new ResolvedConcept(
                concept.Id, concept.Kind, concept.Name, true, concept.Key, concept.DefaultSalaryConstitutive, null, false);
            dtos.Add(new PayrollConceptDto(
                concept.Id, concept.Kind.ToString(), concept.Name, true, concept.Key.ToString(),
                concept.DefaultSalaryConstitutive, null, false, null));
        }

        foreach (var concept in ordered)
        {
            byId[concept.Id.Value] = new ResolvedConcept(
                concept.Id.Value, concept.Kind, concept.Name, false, null, concept.DefaultSalaryConstitutive, concept.Multiplier, concept.Archived);
            dtos.Add(ToDto(concept));
            if (concept.ArchivedAt is { } archived)
            {
                archivedAt[concept.Id.Value] = archived;
            }
        }

        // The importation matches a name without case or accents among the active concepts; on a clash between kinds the system one
        // (inserted first) wins.
        var byName = new Dictionary<string, ResolvedConcept>(StringComparer.Ordinal);
        foreach (var resolved in byId.Values.Where(item => !item.Archived))
        {
            byName.TryAdd(PayrollText.Fold(resolved.Name)!, resolved);
        }

        _byId = byId;
        _activeByFoldedName = byName;
        _dtos = dtos;
        _archivedAt = archivedAt;
    }

    /// <summary>
    /// Los conceptos propios archivados antes de <paramref name="periodStart"/>, contando el día del tenant (decisión del owner 5):
    /// un recurrente de un concepto archivado deja de aplicar desde la quincena siguiente al día del archivo. La quincena en la que
    /// se archivó lo conserva, así un período ya liquidado no cambia de huella. Los del sistema no se archivan.
    /// </summary>
    public IReadOnlySet<Guid> ArchivedBefore(DateOnly periodStart, Func<DateTimeOffset, DateOnly> tenantDay)
    {
        ArgumentNullException.ThrowIfNull(tenantDay);
        return _archivedAt.Where(entry => tenantDay(entry.Value) < periodStart).Select(entry => entry.Key).ToHashSet();
    }

    /// <summary>Del sistema o propio de esa empresa; nulo si no existe (un concepto de otra empresa tampoco).</summary>
    public ResolvedConcept? Find(Guid conceptId) => _byId.GetValueOrDefault(conceptId);

    /// <summary>El concepto no archivado con ese nombre, sin mayúsculas, tildes ni espacios de más; nulo si no hay.</summary>
    public ResolvedConcept? FindActiveByName(string? name) =>
        PayrollText.Fold(name) is { } folded ? _activeByFoldedName.GetValueOrDefault(folded) : null;

    public IReadOnlyList<PayrollConceptDto> ToDtos() => _dtos;

    public static PayrollConceptDto ToDto(PayrollConcept concept) => new(
        concept.Id.Value,
        concept.Kind.ToString(),
        concept.Name,
        false,
        null,
        concept.DefaultSalaryConstitutive,
        concept.Multiplier,
        concept.Archived,
        concept.Version);
}

/// <summary>Carga el catálogo de conceptos de una empresa. Sin caché: es una lectura por solicitud.</summary>
public sealed class ConceptCatalog(IPayrollConceptRepository concepts)
{
    public async Task<ConceptCatalogSnapshot> LoadAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken)
    {
        var own = await concepts.ListAsync(tenantId, companyId, cancellationToken);

        // Defensa en profundidad: el repositorio ya acota por empresa, y un concepto de otra no entra al catálogo.
        return new ConceptCatalogSnapshot(
            own.Where(concept => concept.TenantId == tenantId && concept.CompanyId == companyId).ToList());
    }
}
