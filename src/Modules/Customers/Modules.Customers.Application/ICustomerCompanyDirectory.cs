namespace Modules.Customers.Application;

/// <summary>La empresa de un cliente, como la necesita quien la muestra. <c>Name</c> ya es la etiqueta (A4): <c>BusinessName ?? Name</c>.
/// <c>IdentificationNumber</c> es el de la empresa (nulo si no lo tiene); lo usa la exportación (ruling P5).</summary>
public sealed record CustomerCompanyRef(Guid Id, string Name, bool IsComplete, bool IsActive, string? IdentificationNumber);

public sealed record CustomerContactRef(Guid Id, string Name, string? Phone, string? Email, bool IsComplete, bool IsActive);

/// <summary>Spec 2026-11-10: la relación empresa-contacto vista desde fuera del agregado, siempre acotada al tenant.</summary>
public interface ICustomerCompanyDirectory
{
    /// <summary>Por id de contacto: sólo aparecen los que tienen empresa. Una consulta para toda la página.</summary>
    Task<IReadOnlyDictionary<Guid, CustomerCompanyRef>> FindCompaniesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);

    /// <summary>Contactos sin fusionar de una empresa, por nombre y luego id, hasta <paramref name="take"/>.</summary>
    Task<IReadOnlyList<CustomerContactRef>> ListContactsAsync(
        Guid tenantId, Guid companyId, int take, CancellationToken cancellationToken);

    /// <summary>Por id de empresa: cuántos contactos sin fusionar tiene. Las que no tienen no aparecen.</summary>
    Task<IReadOnlyDictionary<Guid, int>> CountContactsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> companyIds, CancellationToken cancellationToken);

    /// <summary>El contacto, sólo si existe en el tenant, no está fusionado y su empresa es <paramref name="companyId"/>.</summary>
    Task<CustomerContactRef?> FindContactOfAsync(
        Guid tenantId, Guid companyId, Guid contactId, CancellationToken cancellationToken);
}
