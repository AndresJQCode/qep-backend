namespace BuildingBlocks.Domain.Catalog;

/// <summary>La unidad en la que se vende un producto (spec 2026-11-18 §3.2). Lista cerrada que viaja por nombre; la
/// etiqueta la pone el frontend. No depende de <see cref="ProductKind"/>, a propósito.</summary>
public enum SalesUnit
{
    Unit,
    Service,
    Hour,
    Day,
    Week,
    Month,
    Year,
    Kilogram,
    Meter,
    SquareMeter,
    Liter,
}

/// <summary>El código UN/ECE Rec. 20 de cada unidad, el que pide la factura electrónica de la DIAN. Es el único lugar
/// donde vive: hoy QEP no lo usa, pero evita una migración de datos el día que el ERP lo pida.</summary>
public static class SalesUnits
{
    public static string CodeOf(SalesUnit unit) => unit switch
    {
        SalesUnit.Unit => "94",
        SalesUnit.Service => "E48",
        SalesUnit.Hour => "HUR",
        SalesUnit.Day => "DAY",
        SalesUnit.Week => "WEE",
        SalesUnit.Month => "MON",
        SalesUnit.Year => "ANN",
        SalesUnit.Kilogram => "KGM",
        SalesUnit.Meter => "MTR",
        SalesUnit.SquareMeter => "MTK",
        SalesUnit.Liter => "LTR",
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unidad de venta sin código Rec. 20."),
    };
}
