namespace Modules.Quotations.Application;

/// <summary>Lo que Tenancy e Identity saben de la asesora de una cotización.</summary>
/// <param name="Email">El correo, de Identity. Null si el usuario ya no resuelve (dado de baja
/// en Identity). Es lo que muestran listados, historial y ventas.</param>
/// <param name="DisplayName">El nombre que el tenant cargó en la membresía. Null en las
/// membresías anteriores a que existiera y en el owner hasta que alguien lo cargue desde el
/// roster. Hoy lo usa sólo el PDF, que cae al correo cuando falta (spec 2026-09-11, D1 y D6).</param>
public sealed record QuotationAdvisor(string? Email, string? DisplayName);

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
