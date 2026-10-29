namespace Modules.Messaging.Domain;

/// <summary>
/// Spec 2026-10-29: el orden personal en que un asesor ve las respuestas rápidas del tenant. Es sólo presentación: no cambia el catálogo
/// ni lo que ven los demás. Primero van las del orden guardado, en ese orden; después el resto en el orden de siempre (por atajo).
/// </summary>
public static class QuickReplyOrder
{
    /// <summary>Tope de ids por guardado. Un tenant tiene decenas de respuestas; 500 sólo frena un cuerpo abusivo.</summary>
    public const int MaxItems = 500;

    /// <summary>Lo que se guarda: los ids pedidos que siguen siendo del tenant, sin repetir (gana la primera aparición). Lo ajeno no falla
    /// el guardado: un borrado concurrente no debe tumbar el orden de otro asesor.</summary>
    public static IReadOnlyList<Guid> Normalize(IEnumerable<Guid> requested, IEnumerable<Guid> existing)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(existing);
        var known = existing.ToHashSet();
        var seen = new HashSet<Guid>();
        return requested.Where(id => known.Contains(id) && seen.Add(id)).ToArray();
    }

    /// <summary>La lista para quien llama: <paramref name="byShortcut"/> ya viene en el orden de siempre y lo conserva para lo que no está
    /// en <paramref name="savedOrder"/>. Un id guardado que ya no existe (borrado) se salta.</summary>
    public static IReadOnlyList<QuickReply> Arrange(IReadOnlyList<QuickReply> byShortcut, IReadOnlyList<Guid> savedOrder)
    {
        ArgumentNullException.ThrowIfNull(byShortcut);
        ArgumentNullException.ThrowIfNull(savedOrder);
        if (savedOrder.Count == 0)
        {
            return byShortcut;
        }

        var byId = byShortcut.ToDictionary(reply => reply.Id);
        var placed = new HashSet<Guid>();
        var first = savedOrder.Where(id => byId.ContainsKey(id) && placed.Add(id)).Select(id => byId[id]).ToList();
        return first.Concat(byShortcut.Where(reply => !placed.Contains(reply.Id))).ToArray();
    }
}
