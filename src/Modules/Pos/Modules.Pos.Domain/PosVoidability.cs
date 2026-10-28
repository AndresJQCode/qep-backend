namespace Modules.Pos.Domain;

/// <summary>
/// Describe el estado de la venta, no al que pregunta (spec, endpoint 9): sin pos.sale.void la
/// pantalla no dibuja la acción. La primera razón gana.
/// </summary>
public static class PosVoidability
{
    public const string AlreadyVoided = "AlreadyVoided";
    public const string SessionClosed = "SessionClosed";

    public static (bool Voidable, string? BlockedReason) For(
        PosSaleStatus status, CashSessionStatus sessionStatus) =>
        status == PosSaleStatus.Voided ? (false, AlreadyVoided)
        : sessionStatus == CashSessionStatus.Closed ? (false, SessionClosed)
        : (true, null);
}
