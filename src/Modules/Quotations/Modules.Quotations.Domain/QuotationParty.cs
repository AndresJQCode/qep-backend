namespace Modules.Quotations.Domain;

/// <summary>
/// A quién se le factura o a quién se le entrega esta cotización, cuando **no** son los datos del
/// cliente maestro (US-6). Entidad hija de <see cref="Quotation"/> — sin repositorio propio ni
/// construcción fuera del agregado, mismo criterio que <see cref="QuotationItem"/>.
///
/// <para><b>Que la fila no exista es el caso normal</b>: significa "usá los datos del cliente".
/// Por eso el switch de la UI no guarda un booleano — apagarlo crea la fila, prenderlo la borra.
/// Reemplaza a los cuatro <c>*_override</c> que vivían en <c>quotations</c>: ahí el mismo bloque
/// de datos estaba escrito dos veces y a medias (facturación no tenía ciudad, entrega no tenía
/// nombre), y cada parte nueva costaba columnas nuevas.</para>
///
/// <para>Cada campo por separado sigue siendo opcional: null es "para este campo, el del
/// cliente". Es lo que hace que la migración desde los overrides viejos no invente datos que
/// nunca se escribieron.</para>
/// </summary>
public sealed class QuotationParty
{
    // EF Core materializa por acá. El código nunca construye la entidad así: sólo
    // Quotation.Assign hace cumplir los invariantes.
    private QuotationParty()
    {
    }

    private QuotationParty(
        QuotationPartyId id,
        QuotationId quotationId,
        QuotationPartyRole role,
        QuotationPartyDetails details)
    {
        Id = id;
        QuotationId = quotationId;
        Role = role;
        Apply(details);
    }

    public QuotationPartyId Id { get; private set; }

    public QuotationId QuotationId { get; private set; }

    public QuotationPartyRole Role { get; private set; }

    public string? Name { get; private set; }

    public string? Phone { get; private set; }

    public string? Email { get; private set; }

    public string? Address { get; private set; }

    /// <summary>Referencias blandas a <c>geography</c>, igual que <c>Customer.CityId</c> — ids y
    /// no texto libre, que es lo que era <c>delivery_city_override</c>. Sin FK: ningún módulo de
    /// negocio referencia las tablas de otro.</summary>
    public Guid? DepartmentId { get; private set; }

    public Guid? CityId { get; private set; }

    internal static QuotationParty Create(
        QuotationId quotationId,
        QuotationPartyRole role,
        QuotationPartyDetails details) =>
        new(QuotationPartyId.New(), quotationId, role, details);

    // Asigna los seis siempre, incluidos los null: se puede **limpiar** un campo, no sólo
    // setearlo -- mismo criterio que Product.Apply/Customer.Assign.
    internal void Apply(QuotationPartyDetails details)
    {
        var normalized = details.Normalized();
        Name = normalized.Name;
        Phone = normalized.Phone;
        Email = normalized.Email;
        Address = normalized.Address;
        DepartmentId = normalized.DepartmentId;
        CityId = normalized.CityId;
    }
}
