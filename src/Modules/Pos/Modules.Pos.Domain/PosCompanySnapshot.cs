namespace Modules.Pos.Domain;

/// <summary>
/// Empresa emisora congelada al abrir la caja (spec, decisión 7): el ticket es el comprobante que
/// se le entregó al cliente y su reimpresión tiene que decir lo mismo.
/// </summary>
public sealed record PosCompanySnapshot(
    Guid CompanyId, string Name, string TaxId, string? Address, string? Phone);
