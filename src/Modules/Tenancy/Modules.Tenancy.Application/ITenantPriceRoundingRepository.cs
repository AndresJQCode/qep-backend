using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>Se commitea con <see cref="ITenancyUnitOfWork"/>.</summary>
public interface ITenantPriceRoundingRepository
{
    /// <summary>Las filas guardadas del tenant, con tracking: son las que el PUT cambia o borra.</summary>
    Task<IReadOnlyList<TenantPriceRounding>> ListByTenantAsync(TenantId tenantId, CancellationToken cancellationToken);

    void Add(TenantPriceRounding rounding);

    void Remove(TenantPriceRounding rounding);
}
