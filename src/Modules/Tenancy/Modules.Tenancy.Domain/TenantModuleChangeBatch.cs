namespace Modules.Tenancy.Domain;

public sealed record RequestedModuleChange(TenantModuleKey Key, TenantModuleStatus To);

/// <param name="From">null = la fila no existe; activar la crea con origen <c>operator</c>.</param>
public sealed record PlannedModuleChange(TenantModuleKey Key, TenantModuleStatus? From, TenantModuleStatus To);

/// <summary>
/// Spec 2026-10-08 §3: el único camino de dominio para cambiar el estado de los módulos de un tenant
/// (O4). Puro: recibe el estado guardado (inactivas incluidas), el lote y el motivo, y devuelve el plan
/// en el orden de <see cref="TenantModuleKeys.All"/> o falla con uno de cinco códigos. Estricto (O5):
/// la cascada la calcula la consola y llega como un solo lote.
/// </summary>
public static class TenantModuleChangeBatch
{
    public const string NoChangesCode = "tenancy.modules.no_changes";
    public const string DuplicateKeyCode = "tenancy.modules.duplicate_key";
    public const string MixedDirectionsCode = "tenancy.modules.mixed_directions";
    public const string ReasonNotAllowedCode = "tenancy.modules.reason_not_allowed";
    public const string InconsistentDependenciesCode = "tenancy.modules.inconsistent_dependencies";

    public static IReadOnlyList<PlannedModuleChange> Plan(
        IReadOnlyDictionary<TenantModuleKey, TenantModuleStatus> stored,
        IReadOnlyCollection<RequestedModuleChange> requested,
        ChangeReason reason)
    {
        if (requested.Count == 0)
        {
            throw new TenantDomainException(NoChangesCode, "The batch has no changes.");
        }

        if (requested.Select(change => change.Key).Distinct().Count() != requested.Count)
        {
            throw new TenantDomainException(DuplicateKeyCode, "The batch names the same module twice.");
        }

        // D4: la cascada siempre va en una sola dirección; mezclar complicaría el motivo.
        var directions = requested.Select(change => change.To).Distinct().ToArray();
        if (directions.Length > 1)
        {
            throw new TenantDomainException(MixedDirectionsCode, "A batch either activates or deactivates.");
        }

        var to = directions[0];
        var allowed = to == TenantModuleStatus.Active
            ? TenantChangeVocabulary.ModuleActivationReasons
            : TenantChangeVocabulary.ModuleDeactivationReasons;
        if (!allowed.Contains(reason))
        {
            throw new TenantDomainException(
                ReasonNotAllowedCode,
                $"'{TenantChangeVocabulary.ToText(reason)}' is not a reason to {(to == TenantModuleStatus.Active ? "activate" : "deactivate")} a module.");
        }

        var planned = new List<PlannedModuleChange>(requested.Count);
        foreach (var change in requested)
        {
            TenantModuleStatus? from = stored.TryGetValue(change.Key, out var current) ? current : null;
            // Sin cambios vacíos: activar lo activo, desactivar lo inactivo o lo que no tiene fila.
            if ((to == TenantModuleStatus.Active && from == TenantModuleStatus.Active) ||
                (to == TenantModuleStatus.Inactive && from != TenantModuleStatus.Active))
            {
                throw new TenantDomainException(
                    NoChangesCode, $"The '{change.Key.Value}' module is already {TenantChangeVocabulary.ToText(to)} or has no row.");
            }

            planned.Add(new PlannedModuleChange(change.Key, from, to));
        }

        EnsureConsistent(stored, planned);
        return planned.OrderBy(change => IndexOf(change.Key)).ToArray();
    }

    /// <summary>
    /// Regla exacta (§3): con S' el estado después del lote y L sus claves, se rechaza si hay (m, d) con
    /// m activo en S', d dependencia directa de m, d no activo en S', y m ∈ L o d ∈ L. Basta con las
    /// directas: si el lote deja consistente cada par que toca, los transitivos quedan cubiertos. Un par
    /// roto heredado que el lote no toca no bloquea (D9).
    /// </summary>
    private static void EnsureConsistent(
        IReadOnlyDictionary<TenantModuleKey, TenantModuleStatus> stored,
        IReadOnlyList<PlannedModuleChange> planned)
    {
        var after = stored.ToDictionary(entry => entry.Key, entry => entry.Value);
        foreach (var change in planned)
        {
            after[change.Key] = change.To;
        }

        var batch = planned.Select(change => change.Key).ToHashSet();
        bool IsActive(TenantModuleKey key) => after.TryGetValue(key, out var status) && status == TenantModuleStatus.Active;

        var broken = TenantModuleKeys.All
            .Where(IsActive)
            .SelectMany(module => TenantModuleKeys.DependenciesOf(module)
                .Where(dependency => !IsActive(dependency) && (batch.Contains(module) || batch.Contains(dependency)))
                .Select(dependency => $"{module.Value} -> {dependency.Value}"))
            .ToArray();
        if (broken.Length > 0)
        {
            // Sólo para logs: la SPA nunca muestra el detail.
            throw new TenantDomainException(
                InconsistentDependenciesCode,
                $"The batch leaves active modules without their dependencies: {string.Join(", ", broken)}.");
        }
    }

    private static int IndexOf(TenantModuleKey key)
    {
        for (var index = 0; index < TenantModuleKeys.All.Count; index++)
        {
            if (TenantModuleKeys.All[index] == key)
            {
                return index;
            }
        }

        return int.MaxValue;
    }
}
