namespace Modules.Quotations.Application;

/// <summary>
/// Puerto hacia Tenancy/Identity para poner nombre a la asesora de una cotización.
/// <c>Quotation.AdvisorId</c> es un <c>MemberId</c> —una membresía, no un usuario— y el correo
/// vive en Identity, dos saltos que ningún módulo de negocio puede dar por su cuenta: el
/// adaptador vive en <c>Bootstrapper</c>, mismo criterio que
/// <see cref="IQuotationCustomerLookup"/>.
///
/// Batch por la misma razón que <see cref="IQuotationCustomerLookup.FindNamesAsync"/>: el
/// listado necesita todas las asesoras de la página de una vez. Una membresía sin correo
/// resoluble (usuario dado de baja en Identity) devuelve <c>null</c> y la fila muestra el id.
/// </summary>
public interface IQuotationAdvisorLookup
{
    Task<IReadOnlyDictionary<Guid, string?>> FindEmailsAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> membershipIds,
        CancellationToken cancellationToken);
}
