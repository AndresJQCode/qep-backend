using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Reporting.Application;

/// <summary>
/// El resumen del reporte de cotizaciones. Hermano del de ventas —mismos filtros que el listado
/// menos la paginación, mismo permiso, mismo motivo para existir— pero con dos cosas propias que
/// una venta no tiene.
///
/// La primera es <see cref="ByStatus"/>: una cotización vive en cuatro estados y una venta en
/// uno, así que acá el reparto por estado **es** el reporte, no un detalle.
///
/// La segunda es <see cref="Validity"/> y <see cref="Expiring"/>: una cotización enviada tiene
/// fecha de vencimiento, y lo que el negocio necesita saber no es cuántas hay sino **cuánta plata
/// se vence esta semana**. Eso no sale de un listado ordenado por fecha de creación.
///
/// **No hay estado «Aprobada».** Convertir una cotización en venta la deja en <c>Sent</c>; cuántas
/// terminaron en venta se lee en el reporte de ventas, no acá.
/// </summary>
public sealed record QuotationsReportSummaryDto(
    int QuotationCount,
    decimal Subtotal,
    decimal TaxAmount,
    decimal Total,
    IReadOnlyList<ReportMonthlyPointDto> Monthly,
    IReadOnlyList<ReportStatusSliceDto> ByStatus,
    IReadOnlyList<ReportRankEntryDto> ByAdvisor,
    QuotationValidityDto Validity,
    IReadOnlyList<QuotationExpiringDto> Expiring,
    ReportComparisonDto? Previous);

/// <summary>
/// Un estado del enum con su conteo y su monto.
///
/// <c>Status</c> viaja como el nombre del enum (<c>Sent</c>), igual que en
/// <see cref="QuotationsReportItemDto"/>: la traducción es del frontend, que ya tiene el
/// diccionario. **Vienen los cuatro siempre**, incluso en cero: un estado que desaparece de la
/// respuesta obligaría a la pantalla a saber cuáles existen para dibujar el que falta.
/// </summary>
public sealed record ReportStatusSliceDto(string Status, int Count, decimal Total);

/// <summary>
/// Cuánta plata se vence y cuándo, sobre las cotizaciones **en estado <c>Sent</c>**: las únicas
/// que todavía pueden convertirse. Un borrador no vence porque no salió, y una anulada ya no
/// interesa.
///
/// Los tramos se cuentan en días contra hoy, con hoy incluido en el primero. <c>Expired</c> son
/// las que ya pasaron de fecha pero el backend todavía no movió a <c>Expired</c> — que es un
/// estado distinto de este tramo, y por eso los números no coinciden con
/// <see cref="QuotationsReportSummaryDto.ByStatus"/>.
///
/// <c>WithoutExpiry</c> son las enviadas sin <c>ValidUntil</c>. El dominio lo permite
/// (<c>DateOnly?</c>), así que existen y no entran en ningún tramo; se cuentan aparte en vez de
/// desaparecer, porque un total que no cierra sin explicación es peor que un número más.
/// </summary>
public sealed record QuotationValidityDto(
    ReportBucketDto Expired,
    ReportBucketDto WithinSevenDays,
    ReportBucketDto WithinThirtyDays,
    ReportBucketDto Beyond,
    int WithoutExpiry);

/// <summary>Un tramo: cuántas y por cuánto.</summary>
public sealed record ReportBucketDto(int Count, decimal Total);

/// <summary>
/// Una cotización que vence pronto: la única vista de fila que sobrevive en un panel de
/// estadísticas, porque es una **cola de trabajo** — hay algo que hacer con cada renglón antes de
/// una fecha.
///
/// Ordenadas por monto y no por fecha: lo que decide a cuál llamar primero es la plata en juego.
/// </summary>
/// <param name="DaysLeft">Días entre hoy y el vencimiento. Cero es «vence hoy»; nunca es
/// negativo, porque lo ya vencido no entra en esta lista.</param>
/// <param name="AdvisorName">El <b>email</b> del asesor, no su nombre. Ver
/// <see cref="SalesReportItemDto"/>.</param>
public sealed record QuotationExpiringDto(
    Guid QuotationId,
    string QuotationNumber,
    DateOnly ValidUntil,
    int DaysLeft,
    string? ClientName,
    string? ClientCuc,
    string? AdvisorName,
    decimal Total);

/// <summary>
/// Lo que el handler le fija al origen. Va como un record y no como cuatro parámetros sueltos
/// porque tres de los cuatro son topes que sólo el handler decide, y el cuarto —<c>Today</c>— es
/// el que hace que el resultado dependa del reloj: tenerlo explícito en la firma es lo que
/// permite probar los tramos con una fecha fija en vez de con la de la máquina.
/// </summary>
public sealed record QuotationsSummaryOptions(
    int RankSize,
    DateOnly Today,
    int ExpiringWithinDays,
    int ExpiringSize);

/// <summary>Lo que devuelve el origen: el resumen de **una** ventana, sin comparación. Ver
/// <see cref="SalesReportAggregate"/>.</summary>
public sealed record QuotationsReportAggregate(
    int QuotationCount,
    decimal Subtotal,
    decimal TaxAmount,
    decimal Total,
    IReadOnlyList<ReportMonthlyPointDto> Monthly,
    IReadOnlyList<ReportStatusSliceDto> ByStatus,
    IReadOnlyList<ReportRankEntryDto> ByAdvisor,
    QuotationValidityDto Validity,
    IReadOnlyList<QuotationExpiringDto> Expiring);

/// <summary>El resumen agregado del reporte de cotizaciones. Ver
/// <see cref="GetSalesReportSummaryQuery"/>.</summary>
public sealed record GetQuotationsReportSummaryQuery(QuotationsReportFilter Filter)
    : IQuery<QuotationsReportSummaryDto>;

public sealed class GetQuotationsReportSummaryHandler(
    IQuotationsReportSource source,
    IValidator<QuotationsReportFilter> validator,
    IExecutionContext executionContext,
    IClock clock)
    : IQueryHandler<GetQuotationsReportSummaryQuery, QuotationsReportSummaryDto>
{
    public async Task<QuotationsReportSummaryDto> HandleAsync(
        GetQuotationsReportSummaryQuery query,
        CancellationToken cancellationToken)
    {
        // Autorizar primero, siempre: antes de validar y antes de tocar ningún origen de datos.
        ReportingAuthorization.EnsureAuthorized(
            executionContext, query.Filter.TenantId, ReportingPermissions.QuotationRead);
        await validator.ValidateAndThrowAsync(query.Filter, cancellationToken);

        var criteria = query.Filter.ToCriteria();
        // "Hoy" en UTC, el mismo huso en el que se corta el rango de fechas: mezclar dos husos
        // dentro del mismo reporte pondría el borde de un tramo un día corrido del borde del
        // filtro.
        var options = new QuotationsSummaryOptions(
            ReportSummaryRules.RankSize,
            DateOnly.FromDateTime(clock.UtcNow.UtcDateTime),
            ReportSummaryRules.ExpiringWithinDays,
            ReportSummaryRules.ExpiringSize);

        var current = await source.SummarizeAsync(criteria, options, cancellationToken);

        return new QuotationsReportSummaryDto(
            current.QuotationCount,
            current.Subtotal,
            current.TaxAmount,
            current.Total,
            current.Monthly,
            current.ByStatus,
            current.ByAdvisor,
            current.Validity,
            current.Expiring,
            await SummarizePrecedingAsync(criteria, options, cancellationToken));
    }

    /// <summary>
    /// El período anterior, con los mismos filtros y otra ventana. Ver
    /// <c>GetSalesReportSummaryHandler.SummarizePrecedingAsync</c>: se copia el criterio entero
    /// cambiando sólo las fechas, para que asesor, cliente y estado viajen igual.
    ///
    /// De la ventana anterior sólo se usan el conteo y el monto, pero se pide el resumen completo:
    /// una segunda firma en el puerto sólo para esto sería un método más que mantener sincronizado
    /// con el primero.
    /// </summary>
    private async Task<ReportComparisonDto?> SummarizePrecedingAsync(
        QuotationsReportCriteria criteria,
        QuotationsSummaryOptions options,
        CancellationToken cancellationToken)
    {
        if (ReportComparisonWindow.Preceding(criteria.From, criteria.To) is not { } window)
        {
            return null;
        }

        var preceding = await source.SummarizeAsync(
            criteria with { From = window.From, To = window.To },
            // Sin cola de vencimientos ni ranking en la ventana anterior: de ella sólo se lee el
            // conteo y el monto, y pedirlos serían cuatro consultas que nadie mira.
            options with { RankSize = 0, ExpiringSize = 0 },
            cancellationToken);

        return new ReportComparisonDto(preceding.QuotationCount, preceding.Total);
    }
}
