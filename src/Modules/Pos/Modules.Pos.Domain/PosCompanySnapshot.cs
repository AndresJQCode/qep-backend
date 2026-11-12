namespace Modules.Pos.Domain;

/// <summary>
/// Empresa emisora congelada al abrir la caja (spec, decisión 7): el ticket es el comprobante que
/// se le entregó al cliente y su reimpresión tiene que decir lo mismo. El tipo de documento se congela
/// junto con el número: un NIT reimpreso como cédula diría otra cosa aunque el número sea el mismo.
/// </summary>
public sealed record PosCompanySnapshot(
    Guid CompanyId, string Name, string TaxId, string DocumentType, string? Address, string? Phone);
