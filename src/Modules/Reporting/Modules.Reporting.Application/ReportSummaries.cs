namespace Modules.Reporting.Application;

/// <summary>
/// El resumen del reporte de ventas: lo que hace falta para dibujar el panel entero sin bajarse
/// una sola fila.
///
/// Existe porque el listado paginado **no puede** contestar esto. Sumar 418 ventas del lado del
/// cliente serían nueve peticiones de 50 filas y seguiría estando mal en cuanto cambie un filtro;
/// y con el tope de <c>MaxPageSize</c> ni siquiera hay forma de pedir el periodo completo. Un
/// total que se calcula sobre la página que se está mirando es un número equivocado con cara de
/// número correcto.
///
/// Toma **exactamente los mismos filtros que el listado**, menos la paginación — igual que la
/// exportación (<c>ReportExportRules</c>). Que los tres caminos compartan
/// <see cref="SalesReportFilter"/> es lo que hace imposible que el panel, la tabla y el Excel
/// hablen de conjuntos distintos.
/// </summary>
public sealed record SalesReportSummaryDto(
    int SaleCount,
    decimal Subtotal,
    decimal TaxAmount,
    decimal Total,
    IReadOnlyList<ReportMonthlyPointDto> Monthly,
    IReadOnlyList<ReportRankEntryDto> ByAdvisor,
    IReadOnlyList<ReportRankEntryDto> ByClient,
    ReportComparisonDto? Previous);

/// <summary>
/// Un mes de la serie, con su año: sin el año, doce puntos de un rango de dos años se pisan de a
/// pares y la serie miente.
///
/// **Sólo vienen los meses con ventas.** Rellenar los vacíos con cero es una decisión de
/// presentación —depende del rango que el eje dibuje— y se toma en el frontend, no acá.
/// </summary>
public sealed record ReportMonthlyPointDto(int Year, int Month, int Count, decimal Total);

/// <summary>
/// Una fila del ranking por asesor o por cliente.
///
/// <c>Id</c> nulo es **la fila "Otros"**: todo lo que quedó fuera del tope, ya sumado.
/// <c>EntityCount</c> dice cuántas entidades distintas agrupa (1 en una fila normal, el resto en
/// la de "Otros"), que es lo que le permite al frontend escribir "Otros (7)" sin adivinar.
///
/// <c>Label</c> del asesor es su **email**, no un nombre propio — ver
/// <see cref="SalesReportItemDto"/>. <c>Secondary</c> lleva el CUC en el ranking de clientes y
/// viene nulo en el de asesores: un solo record para los dos porque tienen exactamente la misma
/// forma y ninguna razón para divergir.
/// </summary>
public sealed record ReportRankEntryDto(
    Guid? Id,
    string? Label,
    string? Secondary,
    int EntityCount,
    int Count,
    decimal Total);

/// <summary>El mismo cálculo sobre la ventana anterior, recortado a lo que un delta necesita: un
/// panel compara el total y el conteo, no la serie mensual entera.</summary>
public sealed record ReportComparisonDto(int Count, decimal Total);

/// <summary>
/// Lo que devuelve el origen de datos: el resumen de **una** ventana, sin comparación.
///
/// Separado del DTO a propósito. Comparar contra el periodo anterior es una decisión del handler
/// —qué ventana, con qué filtros, y si existe siquiera—, y el adaptador no decide nada: le pasan
/// un criterio y devuelve números. Ver <see cref="ISalesReportSource"/>.
/// </summary>
public sealed record SalesReportAggregate(
    int SaleCount,
    decimal Subtotal,
    decimal TaxAmount,
    decimal Total,
    IReadOnlyList<ReportMonthlyPointDto> Monthly,
    IReadOnlyList<ReportRankEntryDto> ByAdvisor,
    IReadOnlyList<ReportRankEntryDto> ByClient);

/// <summary>Los topes del resumen, que los fija el handler y nunca el llamador.</summary>
public static class ReportSummaryRules
{
    /// <summary>
    /// Cuántas entidades nombradas trae cada ranking antes de plegar el resto en "Otros".
    ///
    /// Es un tope, no una preferencia de diseño: sin él, un tenant con dos mil clientes devuelve
    /// dos mil filas en lo que se supone que es un resumen. Cinco es lo que entra legible en la
    /// tarjeta y deja la fila de "Otros" a la vista, que es la que dice cuánto no se está viendo.
    /// </summary>
    public const int RankSize = 5;

    /// <summary>
    /// Qué tan cerca tiene que estar un vencimiento para entrar en la cola de trabajo del panel
    /// de cotizaciones.
    ///
    /// Siete días y no treinta porque la lista existe para decidir a quién llamar **esta semana**;
    /// con treinta se vuelve un listado y deja de ser una cola. El horizonte largo se mira en los
    /// tramos de <see cref="QuotationValidityDto"/>, que sí llegan hasta más de 30 días.
    /// </summary>
    public const int ExpiringWithinDays = 7;

    /// <summary>Cuántos vencimientos trae la cola. Mismo criterio de tope que
    /// <see cref="RankSize"/>: un resumen no devuelve listados.</summary>
    public const int ExpiringSize = 5;
}

/// <summary>
/// La ventana contra la que se compara un resumen: la de **la misma longitud, inmediatamente
/// anterior**, con las dos puntas inclusivas igual que el filtro.
///
/// Vive suelto y no dentro del handler porque es la regla que le da sentido al "vs. periodo
/// anterior" de cada KPI, y hay más de una razonable: el mes calendario anterior daría otro
/// número. Elegida ésta porque es la única que compara periodos comparables — febrero contra los
/// 28 días previos, no contra los 31 de enero.
/// </summary>
public static class ReportComparisonWindow
{
    /// <summary>
    /// Devuelve la ventana anterior, o <c>null</c> cuando no hay ninguna que tenga sentido: sin
    /// las dos puntas no hay longitud, y un rango dado vuelto no tiene ninguna — el validador ya
    /// lo rechaza antes, y acá no se inventa una ventana negativa por las dudas.
    /// </summary>
    public static (DateOnly From, DateOnly To)? Preceding(DateOnly? from, DateOnly? to)
    {
        if (from is not { } start || to is not { } end || end < start)
        {
            return null;
        }

        var length = end.DayNumber - start.DayNumber + 1;
        var precedingTo = start.AddDays(-1);

        return (precedingTo.AddDays(-(length - 1)), precedingTo);
    }
}
