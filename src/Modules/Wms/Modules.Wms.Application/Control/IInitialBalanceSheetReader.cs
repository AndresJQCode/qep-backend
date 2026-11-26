namespace Modules.Wms.Application;

/// <summary>Una fila de la hoja de carga inicial, en texto crudo por cabecera: ninguna regla de negocio vive en el lector, sólo el parseo
/// estructural. Las celdas vacías llegan <c>null</c>.</summary>
public sealed record InitialBalanceSheetRow(
    int Row, string? Code, string? Location, string? Lot, string? Expiration, string? Quantity, string? Uom);

/// <summary>Un archivo ilegible (corrupto, protegido, un .csv renombrado) es indistinguible de una cabecera equivocada: los dos dan
/// <c>HasExpectedColumns = false</c> y el mismo 422 de archivo inválido.</summary>
public sealed record InitialBalanceSheet(bool HasExpectedColumns, IReadOnlyList<InitialBalanceSheetRow> Rows);

public static class InitialBalanceColumns
{
    public const string Code = "code";
    public const string Location = "location";
    public const string Lot = "lot";
    public const string Expiration = "expiration";
    public const string Quantity = "quantity";
    public const string Uom = "uom";

    public static readonly IReadOnlyList<string> Ordered = [Code, Location, Lot, Expiration, Quantity, Uom];
}

/// <summary>Puerto del parseo del Excel (RF-024). Application no sabe qué librería lo lee; Infrastructure usa ClosedXML.</summary>
public interface IInitialBalanceSheetReader
{
    InitialBalanceSheet Parse(Stream content, CancellationToken cancellationToken);
}
