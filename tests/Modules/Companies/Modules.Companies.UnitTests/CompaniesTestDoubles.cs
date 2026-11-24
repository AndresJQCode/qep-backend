using Modules.Companies.Application;
using Modules.Companies.Domain;

namespace Modules.Companies.UnitTests;

/// <summary>
/// Repositorio en memoria. <c>SearchAsync</c> imita el <c>ILIKE '%x%'</c> real sobre el número de documento (contención sin
/// distinguir mayúsculas), que es justo lo que obliga al alta del sistema a exigir igualdad exacta; <c>status</c> nulo son todas.
/// </summary>
internal sealed class InMemoryCompanyRepository : ICompanyRepository
{
    public List<Company> Companies { get; } = [];

    public Task<IReadOnlyList<Company>> SearchAsync(
        Guid tenantId,
        string? search,
        string? name,
        string? taxId,
        CompanyStatusFilter? status,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Company>>(Companies
            .Where(company => company.TenantId == tenantId)
            .Where(company => taxId is null
                || company.TaxId.Contains(taxId, StringComparison.OrdinalIgnoreCase))
            .Where(company => status switch
            {
                null => true,
                CompanyStatusFilter.Active => company.IsActive,
                _ => !company.IsActive,
            })
            .ToList());

    public Task<Company?> FindAsync(Guid tenantId, CompanyId companyId, CancellationToken cancellationToken) =>
        Task.FromResult(Companies.SingleOrDefault(company => company.TenantId == tenantId && company.Id == companyId));

    public Task<IReadOnlySet<string>> ListCountriesAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

    public Task<bool> AnyWithTaxRateAsync(Guid tenantId, Guid taxRateId, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public void Add(Company company) => Companies.Add(company);

    public void Remove(Company company) => Companies.Remove(company);
}

/// <summary>
/// Unidad de trabajo que anota en <see cref="Log"/>, en orden, <c>begin</c>, <c>lock:{tenantId}</c>, <c>save</c> y
/// <c>commit</c>: es lo que prueba que el candado se toma dentro de la transacción y que se confirma después de guardar.
/// </summary>
internal sealed class NoOpUnitOfWork : ICompaniesUnitOfWork
{
    public List<string> Log { get; } = [];

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        Log.Add("save");
        return Task.FromResult(1);
    }

    public Task<ICompaniesTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        Log.Add("begin");
        return Task.FromResult<ICompaniesTransaction>(new LoggedTransaction(Log));
    }

    public Task LockProvisioningAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        Log.Add($"lock:{tenantId}");
        return Task.CompletedTask;
    }

    private sealed class LoggedTransaction(List<string> log) : ICompaniesTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken)
        {
            log.Add("commit");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
