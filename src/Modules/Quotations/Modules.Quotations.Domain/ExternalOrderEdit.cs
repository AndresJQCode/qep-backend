namespace Modules.Quotations.Domain;

/// <summary>
/// Spec 2026-11-14 §4: una edición que la plataforma (Shopify) hizo sobre un pedido ya importado, con sólo los grupos que se
/// aplican. Cada parte en <c>null</c> no se toca.
/// <list type="bullet">
/// <item><see cref="Lines"/> reemplaza <b>todas</b> las líneas. Llegan preciadas por la plataforma: no se reprecian contra el
/// catálogo.</item>
/// <item><see cref="ShippingParty"/> cambia la parte de envío, o la crea si el pedido no la tenía. Al crearla se quitan de las notas
/// las líneas que empiezan por <see cref="NotesLinePrefix"/> (la dirección cruda que se anotó mientras no había parte).</item>
/// <item><see cref="BuyerNotes"/> sólo se escribe con <see cref="ReplaceBuyerNotes"/>: <c>null</c> también es un valor (la nota
/// se borró en la plataforma).</item>
/// </list>
/// </summary>
public sealed record ExternalOrderEdit(
    IReadOnlyCollection<OrderLineInput>? Lines,
    ShippingDetails? Shipping,
    QuotationPartyDetails? ShippingParty,
    bool ReplaceBuyerNotes,
    string? BuyerNotes,
    string NotesLinePrefix);

/// <summary>Qué grupos cambiaron de verdad: un grupo que llegó igual a lo que el pedido ya tenía no cuenta.</summary>
public sealed record ExternalOrderEditResult(bool LinesChanged, bool ShippingChanged, bool PartyChanged, bool BuyerNotesChanged)
{
    public bool Any => LinesChanged || ShippingChanged || PartyChanged || BuyerNotesChanged;
}

/// <summary>Los grupos de una edición externa, para el historial del pedido (una entrada por grupo aplicado).</summary>
public enum ExternalOrderEditGroup
{
    Lines,
    Shipping,
    Party,
    BuyerNotes,
}
