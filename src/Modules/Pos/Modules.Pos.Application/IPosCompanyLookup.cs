namespace Modules.Pos.Application;

/// <summary>Puerto hacia Companies; el adaptador vive en Bootstrapper.</summary>
public interface IPosCompanyLookup
{
    Task<IReadOnlyList<PosCompanyRef>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Activas e inactivas: el handler decide. Null si no existe en el tenant.</summary>
    Task<PosCompanyRef?> FindAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken);
}

public sealed record PosCompanyRef(Guid Id, string Name, string TaxId, string? Address, string? Phone, bool IsActive);
