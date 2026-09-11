namespace Modules.Quotations.Domain;

/// <summary>
/// A nombre de quién sale la factura cuando la cotización factura a consumidor final
/// (<see cref="Quotation.BillsToFinalConsumer"/>): la figura colombiana de venta a quien no pide
/// factura a su nombre, con el NIT genérico.
///
/// Son constantes y no una fila: no hay nada que editar, y guardarlas en cada cotización sería
/// copiar el mismo texto en cada fila. Es el único lugar del backend donde viven — el PDF y
/// cualquier lector las toman de acá. El frontend las duplica a propósito para mostrarlas antes
/// de guardar; si cambian, cambian en los dos lados.
/// </summary>
public static class FinalConsumer
{
    public const string Name = "Consumidor final";

    public const string IdentificationNumber = "222222222222";
}
