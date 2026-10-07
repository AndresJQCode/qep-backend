namespace Modules.Pos.Domain;

/// <summary>
/// Topes contra errores de digitación, no reglas de negocio (spec, decisión 18). Toda cifra de
/// dinero, descuento o cantidad con más de 2 decimales se rechaza: numeric(14,2) la redondearía en
/// silencio y rompería Σ Amount = Total.
/// </summary>
public static class PosLimits
{
    public const decimal MaxCashAmount = 100_000_000m;
    public const decimal MaxCountedCash = 1_000_000_000m;
    public const decimal MaxQuantity = 99_999m;
    public const int MaxLines = 200;
    public const int MaxPayments = 5;
    public const int ReferenceMaxLength = 60;
    public const int NoteMaxLength = 500;
    public const int VoidReasonMinLength = 3;
    public const int VoidReasonMaxLength = 500;
    public const int CashierNameMaxLength = 320;

    public static bool HasValidScale(decimal value) => value == Math.Round(value, 2);
}
