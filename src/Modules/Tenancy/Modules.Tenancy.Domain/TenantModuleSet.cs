namespace Modules.Tenancy.Domain;

/// <summary>
/// Los módulos de un tenant: los contratados (las filas) y los efectivos. Fail closed: un módulo
/// contratado cuya dependencia no es efectiva cuenta como apagado, y eso se propaga (spec
/// 2026-10-07). Inmutable: volver a prender un módulo es otra consulta, no un cambio acá.
/// </summary>
public sealed class TenantModuleSet
{
    private readonly HashSet<TenantModuleKey> _stored;
    private readonly HashSet<TenantModuleKey> _effective;

    private TenantModuleSet(HashSet<TenantModuleKey> stored, HashSet<TenantModuleKey> effective)
    {
        _stored = stored;
        _effective = effective;
        Stored = TenantModuleKeys.All.Where(stored.Contains).ToArray();
        Effective = TenantModuleKeys.All.Where(effective.Contains).ToArray();
    }

    public static TenantModuleSet Empty { get; } = FromStored([]);

    /// <summary>Contratados, en el orden de <see cref="TenantModuleKeys.All"/>.</summary>
    public IReadOnlyList<TenantModuleKey> Stored { get; }

    /// <summary>Efectivos, en el orden de <see cref="TenantModuleKeys.All"/>.</summary>
    public IReadOnlyList<TenantModuleKey> Effective { get; }

    /// <summary>
    /// Una sola pasada por <see cref="TenantModuleKeys.All"/>, que está en orden topológico: cuando
    /// se mira una clave, todas sus dependencias ya se decidieron. No modifica lo que recorre.
    /// </summary>
    public static TenantModuleSet FromStored(IEnumerable<TenantModuleKey> keys)
    {
        var stored = keys.ToHashSet();
        var effective = new HashSet<TenantModuleKey>();
        foreach (var key in TenantModuleKeys.All)
        {
            if (stored.Contains(key) && TenantModuleKeys.DependenciesOf(key).All(effective.Contains))
            {
                effective.Add(key);
            }
        }

        return new TenantModuleSet(stored, effective);
    }

    public bool IsEnabled(TenantModuleKey key) => _effective.Contains(key);

    public bool IsContracted(TenantModuleKey key) => _stored.Contains(key);

    /// <summary>
    /// Las causas raíz de que una clave contratada esté apagada: las dependencias transitivas que no
    /// están contratadas, en el orden de <see cref="TenantModuleKeys.All"/>. Vacío si la clave no
    /// está contratada o está prendida. Nunca vacío para una contratada y apagada: la cadena de
    /// dependencias no efectivas siempre termina en una sin contratar.
    /// </summary>
    public IReadOnlyList<TenantModuleKey> MissingDependencies(TenantModuleKey key)
    {
        if (!IsContracted(key) || IsEnabled(key))
        {
            return [];
        }

        var closure = Closure(key);
        return TenantModuleKeys.All
            .Where(candidate => closure.Contains(candidate) && !_stored.Contains(candidate))
            .ToArray();
    }

    private static HashSet<TenantModuleKey> Closure(TenantModuleKey key)
    {
        var closure = new HashSet<TenantModuleKey>();
        var pending = new Stack<TenantModuleKey>(TenantModuleKeys.DependenciesOf(key));
        while (pending.Count > 0)
        {
            var dependency = pending.Pop();
            if (closure.Add(dependency))
            {
                foreach (var next in TenantModuleKeys.DependenciesOf(dependency))
                {
                    pending.Push(next);
                }
            }
        }

        return closure;
    }
}
