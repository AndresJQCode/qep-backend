namespace Modules.Pos.Domain;

/// <summary>
/// Empresa emisora congelada al abrir la caja (spec, decisión 7): el ticket es el comprobante que
/// se le entregó al cliente y su reimpresión tiene que decir lo mismo. El tipo de documento se congela
/// junto con el número: un NIT reimpreso como cédula diría otra cosa aunque el número sea el mismo.
/// <see cref="ChargesVat"/> (spec 2026-11-17 R4) también se congela: cambiar el IVA de la empresa con la
/// caja abierta no cambia lo que esa caja cobra.
/// </summary>
public sealed record PosCompanySnapshot(
    Guid CompanyId, string Name, string TaxId, string DocumentType, string? Address, string? Phone, bool ChargesVat = true);
