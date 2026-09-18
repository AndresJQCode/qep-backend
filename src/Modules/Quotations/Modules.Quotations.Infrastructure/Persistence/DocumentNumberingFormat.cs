namespace Modules.Quotations.Infrastructure.Persistence;

/// <summary>
/// El formato del número de un tipo de documento de un tenant (spec 2026-09-17). Una fila por
/// (tenant, tipo); sin fila, el lector devuelve el default y nada cambia.
///
/// Vive en Infrastructure y no en Domain, mismo criterio que <see cref="QuotationNumberCounter"/>:
/// no es una regla de negocio, es la fila que la configuración escribe. El valor validado que la
/// aplicación consume es <c>DocumentNumberFormat</c>, en Application.
/// </summary>
internal sealed class DocumentNumberingFormat
{
    public Guid TenantId { get; init; }

    /// <summary><c>order</c> o <c>quotation</c>, con su CHECK en la base. El adaptador traduce
    /// desde <c>DocumentNumberType</c>.</summary>
    public string DocumentType { get; init; } = string.Empty;

    public string Prefix { get; init; } = string.Empty;

    public bool IncludeYear { get; init; }

    /// <summary>Vacío, <c>-</c> o <c>/</c>. Sólo se usa si <see cref="IncludeYear"/>.</summary>
    public string YearSeparator { get; init; } = string.Empty;

    public int MinDigits { get; init; }
}
