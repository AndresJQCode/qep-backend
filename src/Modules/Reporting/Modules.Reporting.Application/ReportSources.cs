using Modules.Reporting.Domain;

namespace Modules.Reporting.Application;

/// <summary>
/// De donde salen las ventas del reporte.
///
/// **Puerto aca, adaptador en <c>Bootstrapper</c>** — mismo patron que
/// <c>IProductImageLookup</c> (CAT-05) y <c>ICustomerGeographyLookup</c>.
/// <c>Modules.Reporting.Application</c> no puede referenciar a <c>Modules.Quotations.*</c>,
/// <c>Modules.Customers.*</c>, <c>Modules.Catalog.*</c> ni <c>Modules.Identity.*</c>:
/// <c>ReportingLayerTests.ApplicationOnlyReferencesAnotherBusinessModule</c> lo prohibe.
/// Reporting es lectura pura sobre datos de otros modulos, asi que **todos** sus origenes cruzan
/// una frontera y el composition root es el unico lugar donde ese acoplamiento es legitimo.
///
/// El adaptador devuelve el DTO ya armado —incluidos el email del asesor y el nombre del
/// cliente, resueltos en lote para la pagina entera— porque los datos con los que se arma viven
/// del otro lado de la frontera.
/// </summary>
public interface ISalesReportSource
{
    Task<(IReadOnlyList<SalesReportItemDto> Items, int Total)> ListAsync(
        SalesReportCriteria criteria,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// Las filas de la exportacion, sin paginar.
    ///
    /// <paramref name="limit"/> **no** es el tope del contrato: el handler pide uno mas que el
    /// tope justamente para poder distinguir "entro justo" de "se paso" y tirar
    /// <c>reporting.export.too_many_rows</c>. El origen solo tiene que respetarlo.
    /// </summary>
    Task<IReadOnlyList<SalesReportItemDto>> ListForExportAsync(
        SalesReportCriteria criteria,
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>Ver <see cref="ISalesReportSource"/>.</summary>
public interface IQuotationsReportSource
{
    Task<(IReadOnlyList<QuotationsReportItemDto> Items, int Total)> ListAsync(
        QuotationsReportCriteria criteria,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<QuotationsReportItemDto>> ListForExportAsync(
        QuotationsReportCriteria criteria,
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>
/// Ver <see cref="ISalesReportSource"/>.
///
/// **Este devuelve filas crudas y no el DTO**, a diferencia de los otros tres:
/// <c>Difference</c> es un valor derivado con una regla propia —un lado nulo cuenta como cero,
/// ver <see cref="PriceChangeDifference"/>—, y una regla no se calcula en el composition root,
/// donde ninguna prueba unitaria la alcanza.
/// </summary>
public interface IPriceChangeReportSource
{
    Task<(IReadOnlyList<PriceChangeReportRow> Rows, int Total)> ListAsync(
        PriceChangeReportCriteria criteria,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PriceChangeReportRow>> ListForExportAsync(
        PriceChangeReportCriteria criteria,
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>Una fila del historico tal como esta guardada, sin la diferencia calculada.</summary>
public sealed record PriceChangeReportRow(
    Guid ChangeId,
    Guid ProductId,
    string ProductCode,
    string ProductName,
    PriceChangeField Field,
    int? ScaleFromUnit,
    int? ScaleToUnit,
    decimal? PreviousValue,
    decimal? NewValue,
    Guid ChangedById,
    string? ChangedByName,
    DateTimeOffset ChangedAt);

/// <summary>Ver <see cref="ISalesReportSource"/>.</summary>
public interface ICustomerReportSource
{
    Task<(IReadOnlyList<CustomerReportItemDto> Items, int Total)> ListAsync(
        CustomerReportCriteria criteria,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CustomerReportItemDto>> ListForExportAsync(
        CustomerReportCriteria criteria,
        int limit,
        CancellationToken cancellationToken);
}
