namespace Modules.Quotations.Application;

/// <summary>Lo que Tenancy e Identity saben de la asesora de una cotización.</summary>
/// <param name="Email">El correo, de Identity. Null si el usuario ya no resuelve (dado de baja
/// en Identity). Es lo que muestran el detalle, el historial y el listado de pedidos.</param>
/// <param name="DisplayName">El nombre que el tenant cargó en la membresía. Null en las
/// membresías anteriores a que existiera y en las creadas con <c>CreateActive</c> —el owner al
/// registrarse y los miembros sembrados— hasta que alguien lo cargue desde el roster.</param>
/// <param name="AdvisorCode">El código con el que el sistema externo del tenant identifica a la
/// asesora (spec 2026-09-24). Es el de hoy, no uno congelado al vender (D9): si se lo cambian, los
/// pedidos viejos salen con el nuevo. Null si la membresía no tiene código.</param>
public sealed record QuotationAdvisor(string? Email, string? DisplayName, int? AdvisorCode)
{
    /// <summary>Cómo presentar a la asesora donde se la muestra por nombre —el listado de
    /// cotizaciones y su Excel—: el nombre o, mientras la membresía no tenga uno, el correo
    /// (spec 2026-09-11, D1, nota del 2026-09-14). Es el mismo respaldo que aplica el PDF sobre
    /// <c>QuotationResponse</c> (D6). Null sólo si no hay ninguno de los dos.</summary>
    public string? Label => DisplayName ?? Email;
}

/// <summary>
/// Puerto hacia Tenancy/Identity para poner nombre a la asesora de una cotización.
/// <c>Quotation.AdvisorId</c> es un <c>MemberId</c> —una membresía, no un usuario—: el nombre
/// vive en esa membresía y el correo en Identity, dos lugares que ningún módulo de negocio puede
/// leer por su cuenta. El adaptador vive en <c>Bootstrapper</c>, mismo criterio que
/// <see cref="IQuotationCustomerLookup"/>.
///
/// Batch por la misma razón que <see cref="IQuotationCustomerLookup.FindNamesAsync"/>: el
/// listado necesita todas las asesoras de la página de una vez. Una membresía que no es del
/// tenant no aparece en el diccionario.
/// </summary>
public interface IQuotationAdvisorLookup
{
    Task<IReadOnlyDictionary<Guid, QuotationAdvisor>> FindAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> membershipIds,
        CancellationToken cancellationToken);
}
