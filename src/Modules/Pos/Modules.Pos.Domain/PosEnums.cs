namespace Modules.Pos.Domain;

// Se persisten y viajan por nombre: el diccionario de etiquetas lo tiene el frontend.
public enum CashSessionStatus
{
    Open,
    Closed,
}

public enum PosSaleStatus
{
    Completed,
    Voided,
}

public enum PosPaymentMethod
{
    Cash,
    Card,
    Transfer,
}
