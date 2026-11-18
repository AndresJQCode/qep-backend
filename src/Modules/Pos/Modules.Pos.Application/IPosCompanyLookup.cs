namespace Modules.Pos.Application;

/// <summary>Puerto hacia Companies; el adaptador vive en Bootstrapper.</summary>
public interface IPosCompanyLookup
{
    Task<IReadOnlyList<PosCompanyRef>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Activas e inactivas: el handler decide. Null si no existe en el tenant.</summary>
    Task<PosCompanyRef?> FindAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken);
}

/// <summary><see cref="ChargesVat"/>: si la empresa cobra IVA (spec 2026-11-17 §5.1); la caja lo congela al abrir (R4).</summary>
public sealed record PosCompanyRef(
    Guid Id, string Name, string TaxId, string DocumentType, string? Address, string? Phone, bool IsActive, bool ChargesVat = true);
