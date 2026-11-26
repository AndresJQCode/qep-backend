using Microsoft.EntityFrameworkCore;
using Modules.Customers.Application;
using Modules.Customers.Domain;

namespace Modules.Customers.Infrastructure.Persistence;

/// <summary>Spec 2026-11-10: lecturas de la relación empresa-contacto. Cada consulta filtra por tenant y no rastrea entidades.</summary>
internal sealed class CustomerCompanyDirectory(CustomersDbContext dbContext) : ICustomerCompanyDirectory
{
    // A4: la etiqueta es la razón social cuando tiene texto, y el nombre si no. Se calcula en .NET sobre columnas
    // proyectadas, no en la consulta, para no depender de cómo traduce EF IsNullOrWhiteSpace.
    private static string Label(string? businessName, string name) =>
        string.IsNullOrWhiteSpace(businessName) ? name : businessName;

    public async Task<IReadOnlyDictionary<Guid, CustomerCompanyRef>> FindCompaniesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(customerIds);
        if (customerIds.Count == 0)
        {
            return new Dictionary<Guid, CustomerCompanyRef>();
        }

        var ids = customerIds.Distinct().Select(id => new CustomerId(id)).ToArray();
        var rows = await dbContext.Customers
            .AsNoTracking()
            .Where(contact => contact.TenantId == tenantId && ids.Contains(contact.Id) && contact.CompanyId != null)
            .Join(
                dbContext.Customers.Where(company => company.TenantId == tenantId),
                contact => contact.CompanyId,
                company => company.Id,
                (contact, company) => new
                {
                    ContactId = contact.Id,
                    CompanyId = company.Id,
                    company.Name,
                    company.BusinessName,
                    company.Completeness,
                    company.IsActive,
                    company.IdentificationNumber
                })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            row => row.ContactId.Value,
            row => new CustomerCompanyRef(
                row.CompanyId.Value,
                Label(row.BusinessName, row.Name),
                row.Completeness == CustomerCompleteness.Complete,
                row.IsActive,
                row.IdentificationNumber));
    }

    public async Task<IReadOnlyList<CustomerContactRef>> ListContactsAsync(
        Guid tenantId, Guid companyId, int take, CancellationToken cancellationToken)
    {
        var company = new CustomerId(companyId);
        var rows = await dbContext.Customers
            .AsNoTracking()
            .Where(contact => contact.TenantId == tenantId && contact.CompanyId == company && contact.MergedIntoId == null)
            .OrderBy(contact => contact.Name).ThenBy(contact => contact.Id)
            .Take(take)
            .Select(contact => new { contact.Id, contact.Name, contact.Phone, contact.Email, contact.Completeness, contact.IsActive })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new CustomerContactRef(
                row.Id.Value, row.Name, row.Phone, row.Email, row.Completeness == CustomerCompleteness.Complete, row.IsActive))
            .ToArray();
    }

    public async Task<IReadOnlyDictionary<Guid, int>> CountContactsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> companyIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(companyIds);
        if (companyIds.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        var ids = companyIds.Distinct().Select(id => new CustomerId(id)).ToArray();
        var rows = await dbContext.Customers
            .AsNoTracking()
            .Where(contact => contact.TenantId == tenantId && contact.CompanyId != null && ids.Contains(contact.CompanyId.Value)
                && contact.MergedIntoId == null)
            .GroupBy(contact => contact.CompanyId!.Value)
            .Select(group => new { CompanyId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(row => row.CompanyId.Value, row => row.Count);
    }

    public async Task<CustomerContactRef?> FindContactOfAsync(
        Guid tenantId, Guid companyId, Guid contactId, CancellationToken cancellationToken)
    {
        var company = new CustomerId(companyId);
        var contact = new CustomerId(contactId);
        var row = await dbContext.Customers
            .AsNoTracking()
            .Where(candidate => candidate.TenantId == tenantId && candidate.Id == contact && candidate.CompanyId == company
                && candidate.MergedIntoId == null)
            .Select(candidate => new { candidate.Id, candidate.Name, candidate.Phone, candidate.Email, candidate.Completeness, candidate.IsActive })
            .SingleOrDefaultAsync(cancellationToken);

        return row is null
            ? null
            : new CustomerContactRef(row.Id.Value, row.Name, row.Phone, row.Email, row.Completeness == CustomerCompleteness.Complete, row.IsActive);
    }
}
