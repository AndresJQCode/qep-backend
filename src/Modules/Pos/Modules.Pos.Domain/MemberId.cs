namespace Modules.Pos.Domain;

/// <summary>
/// La persona en un documento es su membresía, no su usuario (mismo modelo que Quotations). Tipo
/// propio porque el dominio no referencia a Tenancy.
/// </summary>
public readonly record struct MemberId(Guid Value);
