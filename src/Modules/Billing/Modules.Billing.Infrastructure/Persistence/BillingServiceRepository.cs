using Microsoft.EntityFrameworkCore;
using Modules.Billing.Application;
using Modules.Billing.Domain;

namespace Modules.Billing.Infrastructure.Persistence;

internal sealed class BillingServiceRepository(BillingDbContext dbContext) : IBillingServiceRepository
{
    public void Add(BillingService service) => dbContext.Services.Add(service);

    public Task<BillingService?> FindAsync(
        Guid tenantId, BillingServiceId id, CancellationToken cancellationToken) =>
        dbContext.Services.FirstOrDefaultAsync(
            service => service.TenantId == tenantId && service.Id == id, cancellationToken);

    public async Task<(IReadOnlyList<BillingService> Items, int Total)> ListAsync(
        BillingServiceQuery query, CancellationToken cancellationToken)
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

        var total = await services.CountAsync(cancellationToken);
        // El spec ordena por cliente y luego nombre, pero el nombre del cliente no está en este
        // esquema; se ordena por el nombre del servicio (con Id de desempate para paginar estable).
        var items = await services
            .OrderBy(service => service.Name)
            .ThenBy(service => service.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<IReadOnlyList<BillingService>> ListActiveAsync(
        Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.Services
            .Where(service => service.TenantId == tenantId && service.Status == BillingServiceStatus.Active)
            .ToListAsync(cancellationToken);
}
