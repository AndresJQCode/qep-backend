using Modules.Customers.Application;
using Modules.Customers.Domain;

namespace Modules.Customers.UnitTests;

/// <summary>
/// El doble de <see cref="ICustomerCompanyDirectory"/> para las pruebas de los casos de uso: lee de la lista de clientes que la
/// prueba le entrega, con las mismas reglas del directorio real (por tenant, sin fusionados, etiqueta <c>BusinessName ?? Name</c>).
/// </summary>
internal sealed class FakeCustomerCompanyDirectory(IEnumerable<Customer> customers) : ICustomerCompanyDirectory
{
    private readonly List<Customer> _customers = [.. customers];

    /// <summary>Cada llamada a <see cref="FindCompaniesAsync"/>: lo que se pide en lote se cuenta una vez, no por fila.</summary>
    public List<IReadOnlyCollection<Guid>> FindCompaniesCalls { get; } = [];

    public Task<IReadOnlyDictionary<Guid, CustomerCompanyRef>> FindCompaniesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        FindCompaniesCalls.Add(customerIds);
        var result = new Dictionary<Guid, CustomerCompanyRef>();
        foreach (var contact in _customers.Where(customer => customer.TenantId == tenantId
                     && customerIds.Contains(customer.Id.Value) && customer.CompanyId is not null))
        {
            var company = _customers.SingleOrDefault(candidate => candidate.TenantId == tenantId && candidate.Id == contact.CompanyId);
            if (company is not null)
            {
                result[contact.Id.Value] = new CustomerCompanyRef(
                    company.Id.Value,
                    string.IsNullOrWhiteSpace(company.BusinessName) ? company.Name : company.BusinessName,
                    company.Completeness == CustomerCompleteness.Complete,
                    company.IsActive,
                    company.IdentificationNumber);
            }
        }

        return Task.FromResult<IReadOnlyDictionary<Guid, CustomerCompanyRef>>(result);
    }

    public Task<IReadOnlyList<CustomerContactRef>> ListContactsAsync(
        Guid tenantId, Guid companyId, int take, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CustomerContactRef>>(ContactsOf(tenantId, companyId)
            .OrderBy(contact => contact.Name, StringComparer.Ordinal).ThenBy(contact => contact.Id.Value)
            .Take(take)
            .Select(ToRef)
            .ToArray());

    public Task<IReadOnlyDictionary<Guid, int>> CountContactsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> companyIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, int>>(companyIds.Distinct()
            .Select(id => (Id: id, Count: ContactsOf(tenantId, id).Count()))
            .Where(pair => pair.Count > 0)
            .ToDictionary(pair => pair.Id, pair => pair.Count));

    public Task<CustomerContactRef?> FindContactOfAsync(
        Guid tenantId, Guid companyId, Guid contactId, CancellationToken cancellationToken) =>
        Task.FromResult(ContactsOf(tenantId, companyId)
            .Where(contact => contact.Id.Value == contactId)
            .Select(ToRef)
            .SingleOrDefault());

    private IEnumerable<Customer> ContactsOf(Guid tenantId, Guid companyId) =>
        _customers.Where(customer => customer.TenantId == tenantId
            && customer.CompanyId?.Value == companyId && customer.MergedIntoId is null);

    private static CustomerContactRef ToRef(Customer contact) => new(
        contact.Id.Value, contact.Name, contact.Phone, contact.Email,
        contact.Completeness == CustomerCompleteness.Complete, contact.IsActive);
}
