using Microsoft.EntityFrameworkCore;
using Modules.ServiceContracts.Application;
using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.Infrastructure.Persistence;

internal sealed class ServiceContractRepository(ServiceContractsDbContext dbContext) : IServiceContractRepository
{
    public void Add(ServiceContract service) => dbContext.Services.Add(service);

    public Task<ServiceContract?> FindAsync(
        Guid tenantId, ServiceContractId id, CancellationToken cancellationToken) =>
        dbContext.Services.FirstOrDefaultAsync(
            service => service.TenantId == tenantId && service.Id == id, cancellationToken);

    public async Task<IReadOnlyList<ServiceContract>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<ServiceContractId> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var wanted = ids.ToArray();
        return await dbContext.Services
            .AsNoTracking()
            .Where(service => service.TenantId == tenantId && wanted.Contains(service.Id))
            .ToListAsync(cancellationToken);
    }

    // Los filtros del listado, compartidos por la página y por ListAllAsync.
    private IQueryable<ServiceContract> Filtered(ServiceContractQuery query)
    {
        var services = dbContext.Services
            .AsNoTracking()
            .Where(service => service.TenantId == query.TenantId);

        if (query.CustomerId is { } customerId)
        {
            services = services.Where(service => service.CustomerId == customerId);
        }

        if (query.Status is { } status)
        {
            services = services.Where(service => service.Status == status);
        }

        // Basta con que coincida el nombre del servicio o que el cliente esté entre los que el puerto
        // de clientes ya resolvió por nombre: el nombre del cliente vive en otro esquema.
        var pattern = LikeSearch.ContainsPattern(query.SearchTerm);
        var customerIds = query.SearchCustomerIds is { Count: > 0 } ids ? ids.ToArray() : [];
        if (pattern is not null)
        {
            services = services.Where(service =>
                EF.Functions.ILike(service.Name, pattern, LikeSearch.EscapeCharacter) ||
                customerIds.Contains(service.CustomerId));
        }

        return services;
    }

    public async Task<IReadOnlyList<ServiceContract>> ListAllAsync(
        ServiceContractQuery query, CancellationToken cancellationToken) =>
        await Filtered(query)
            .OrderBy(service => service.Name)
            .ThenBy(service => service.Id)
            .ToListAsync(cancellationToken);

    public async Task<(IReadOnlyList<ServiceContract> Items, int Total)> ListAsync(
        ServiceContractQuery query, CancellationToken cancellationToken)
    {
        var services = Filtered(query);

        var total = await services.CountAsync(cancellationToken);
        // El spec §4 ordena por cliente y luego nombre, pero el nombre del cliente no está en este
        // esquema (ruling del controller, fix round 1): el handler de Task 7 usa ListAllAsync, ordena
        // por cliente con IServiceContractsCustomerLookup y pagina en memoria. Acá se ordena por el nombre del servicio (con Id de desempate para paginar estable).
        var items = await services
            .OrderBy(service => service.Name)
            .ThenBy(service => service.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<IReadOnlyList<ServiceContract>> ListActiveAsync(
        Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.Services
            .Where(service => service.TenantId == tenantId && service.Status == ServiceContractStatus.Active)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ServiceContract>> ListOpenByCustomersAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        if (customerIds.Count == 0)
        {
            return [];
        }

        var wanted = customerIds.Distinct().ToArray();
        return await dbContext.Services.AsNoTracking()
            .Where(service => service.TenantId == tenantId
                && wanted.Contains(service.CustomerId)
                && (service.Status == ServiceContractStatus.Active || service.Status == ServiceContractStatus.Paused))
            .ToListAsync(cancellationToken);
    }
}
