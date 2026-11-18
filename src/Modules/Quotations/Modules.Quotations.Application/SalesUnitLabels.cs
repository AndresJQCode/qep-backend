using BuildingBlocks.Domain.Catalog;

namespace Modules.Quotations.Application;

/// <summary>
/// La etiqueta en español de cada unidad de venta, para el PDF (spec 2026-11-18 §6). La pantalla tiene su propio
/// diccionario —los enums viajan por nombre—, pero el PDF se arma en el backend y necesita el suyo. Cerrado y en
/// texto plano: la plantilla lo imprime como cadena, nunca lo evalúa. El plural lo decide la cantidad: singular sólo
/// con exactamente 1. Las abreviaturas (kg, m, m², l) no tienen plural.
/// </summary>
internal static class SalesUnitLabels
{
    public static string Spanish(SalesUnit unit, decimal quantity)
    {
        var singular = quantity == 1m;
        return unit switch
        {
            SalesUnit.Unit => singular ? "unidad" : "unidades",
            SalesUnit.Service => singular ? "servicio" : "servicios",
            SalesUnit.Hour => singular ? "hora" : "horas",
            SalesUnit.Day => singular ? "día" : "días",
            SalesUnit.Week => singular ? "semana" : "semanas",
            SalesUnit.Month => singular ? "mes" : "meses",
            SalesUnit.Year => singular ? "año" : "años",
            SalesUnit.Kilogram => "kg",
            SalesUnit.Meter => "m",
            SalesUnit.SquareMeter => "m²",
            SalesUnit.Liter => "l",
            _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unidad de venta sin etiqueta en español."),
        };
    }
}
