namespace Modules.Hr.Application;

/// <summary>Puerto hacia Companies; el adaptador vive en Bootstrapper.</summary>
public interface IHrCompanyLookup
{
    Task<IReadOnlyList<HrCompanyRef>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Activas e inactivas: el handler decide. Null si no existe en el tenant.</summary>
    Task<HrCompanyRef?> FindAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken);
}

public sealed record HrCompanyRef(Guid Id, string Name, string TaxId, bool IsActive, string Country = "CO");
