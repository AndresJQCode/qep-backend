using Microsoft.EntityFrameworkCore;
using Modules.Wholesale.Application;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Infrastructure.Persistence;

internal sealed class DistributorRepository(WholesaleDbContext dbContext) : IDistributorRepository
{
    public Task<Distributor?> GetAsync(Guid tenantId, DistributorId id, CancellationToken cancellationToken) =>
        dbContext.Distributors.FirstOrDefaultAsync(
            distributor => distributor.TenantId == tenantId && distributor.Id == id, cancellationToken);

    // La única lectura sin tenant: el hash es la llave y quien abre el link no tiene uno. Rastreada: la aceptación escribe.
    public Task<Distributor?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        dbContext.Distributors.FirstOrDefaultAsync(
            distributor => distributor.Invitation.TokenHash == tokenHash, cancellationToken);

    public Task<bool> IdentificationExistsAsync(
        Guid tenantId,
        string identificationType,
        string identificationNumber,
        DistributorId? excluding,
        CancellationToken cancellationToken)
    {
        var trimmed = identificationNumber.Trim();
        var matching = dbContext.Distributors.AsNoTracking().Where(
            distributor => distributor.TenantId == tenantId
                && distributor.IdentificationType == identificationType
                && distributor.IdentificationNumber == trimmed);
        if (excluding is { } own)
        {
            matching = matching.Where(distributor => distributor.Id != own);
        }

        return matching.AnyAsync(cancellationToken);
    }

    // Sin rastreo: la fase 3 sólo lee la relación para decidir acceso y precio; nunca la escribe por acá.
    public Task<Distributor?> FindByTenantsAsync(
        Guid wholesalerTenantId, Guid distributorTenantId, CancellationToken cancellationToken) =>
        dbContext.Distributors.AsNoTracking().FirstOrDefaultAsync(
            distributor => distributor.TenantId == wholesalerTenantId && distributor.DistributorTenantId == distributorTenantId,
            cancellationToken);

    public Task<bool> IsLinkedAsync(Guid wholesalerTenantId, Guid distributorTenantId, CancellationToken cancellationToken) =>
        dbContext.Distributors.AsNoTracking().AnyAsync(
            distributor => distributor.TenantId == wholesalerTenantId && distributor.DistributorTenantId == distributorTenantId,
            cancellationToken);

    public async Task<IReadOnlySet<Guid>> LinkedTenantIdsAsync(Guid wholesalerTenantId, CancellationToken cancellationToken)
    {
        var ids = await dbContext.Distributors.AsNoTracking()
            .Where(distributor => distributor.TenantId == wholesalerTenantId && distributor.DistributorTenantId != null)
            .Select(distributor => distributor.DistributorTenantId!.Value)
            .ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }

    public async Task<(IReadOnlyList<Distributor> Items, int Total)> SearchAsync(
        Guid tenantId,
        DistributorStatus? status,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var distributors = Filtered(tenantId, search);
        if (status is { } wanted)
        {
            distributors = distributors.Where(distributor => distributor.Status == wanted);
        }

        var total = await distributors.CountAsync(cancellationToken);
        var items = await distributors
            .OrderByDescending(distributor => distributor.CreatedAt)
            .ThenByDescending(distributor => distributor.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<IReadOnlyDictionary<DistributorStatus, int>> CountByStatusAsync(
        Guid tenantId, string? search, CancellationToken cancellationToken)
    {
        // Una sola ida con GROUP BY; un estado sin filas no viene y el handler lo completa en 0.
        var rows = await Filtered(tenantId, search)
            .GroupBy(distributor => distributor.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(row => row.Status, row => row.Count);
    }

    public void Add(Distributor distributor) => dbContext.Distributors.Add(distributor);

    public void Remove(Distributor distributor) => dbContext.Distributors.Remove(distributor);

    // Razón social, documento o correo, con ILIKE escapado.
    private IQueryable<Distributor> Filtered(Guid tenantId, string? search)
    {
        var distributors = dbContext.Distributors.AsNoTracking().Where(distributor => distributor.TenantId == tenantId);
        var pattern = LikeSearch.ContainsPattern(search);
        if (pattern is not null)
        {
            distributors = distributors.Where(distributor =>
                EF.Functions.ILike(distributor.BusinessName, pattern, LikeSearch.EscapeCharacter)
                || EF.Functions.ILike(distributor.IdentificationNumber, pattern, LikeSearch.EscapeCharacter)
                || EF.Functions.ILike(distributor.Email, pattern, LikeSearch.EscapeCharacter));
        }

        return distributors;
    }
}
