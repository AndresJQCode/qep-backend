using BuildingBlocks.Application;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

/// <summary>
/// Exporta el catalogo que dejan los filtros del listado — el conjunto completo que coincide, no
/// la pagina que se esta mirando. Mismos filtros que <see cref="ListProductsQuery"/> menos
/// <c>Search</c>: ese existe para el combobox de cotizaciones, no para el listado desde donde se
/// exporta.
/// </summary>
public sealed record ExportProductsQuery(
    Guid TenantId,
    string? Name,
    string? Code,
    bool? IsActive) : IQuery<ProductExportAccepted>;

/// <summary>
/// Lo que contesta el 202. No trae el enlace a proposito: el archivo viaja por correo, y
/// devolverlo aca abriria un segundo canal de entrega que invita a saltear el primero — mismo
/// criterio que la exportacion de clientes del frontend.
/// </summary>
public sealed record ProductExportAccepted(
    string FileName,
    int ProductCount,
    DateTimeOffset ExpiresAt,
    bool EmailSent);

public sealed class ExportProductsHandler(
    IProductRepository repository,
    ITaxRateRepository taxRateRepository,
    IProductExportWorkbookBuilder workbookBuilder,
    IProductExportDelivery delivery,
    IExecutionContext executionContext,
    IClock clock)
    : IQueryHandler<ExportProductsQuery, ProductExportAccepted>
{
    /// <summary>
    /// Tope de filas, mismo criterio que la exportacion de clientes: por encima de esto el Excel
    /// deja de ser algo que alguien abre y el armado empieza a competir por memoria con el
    /// resto del proceso. Se corta con un 422 que dice que hay que acotar con los filtros, no
    /// con un timeout.
    /// </summary>
    private const int MaxRows = 20_000;

    public async Task<ProductExportAccepted> HandleAsync(
        ExportProductsQuery query,
        CancellationToken cancellationToken)
    {
        CatalogAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, CatalogPermissions.ProductRead);

        var products = await repository.ListForExportAsync(
            query.TenantId, query.Name, query.Code, query.IsActive, cancellationToken);

        if (products.Count == 0)
        {
            throw new CatalogDomainException(
                "catalog.export.empty",
                "There are no products to export for the given filters.");
        }

        if (products.Count > MaxRows)
        {
            throw new CatalogDomainException(
                "catalog.export.too_many_rows",
                $"The export exceeds the maximum of {MaxRows} products.");
        }

        // Las tasas se resuelven de una sola vez para todo el lote: el producto guarda el id y
        // la planilla muestra el nombre, y pedirla producto por producto seria el N+1 clasico.
        var taxRates = await taxRateRepository.ListAsync(query.TenantId, cancellationToken);
        var taxRateNames = taxRates.ToDictionary(rate => rate.Id, rate => rate.Name);

        var rows = products.Select(product => ToRow(product, taxRateNames)).ToList();
        var content = workbookBuilder.Build(rows);
        var fileName = FileNameFor(clock.UtcNow);

        var delivered = await delivery.DeliverAsync(
            query.TenantId, fileName, content, cancellationToken);

        return new ProductExportAccepted(
            fileName, products.Count, delivered.ExpiresAt, delivered.EmailSent);
    }

    private static ProductExportRow ToRow(
        Product product, IReadOnlyDictionary<TaxRateId, string> taxRateNames) =>
        new(
            product.Code,
            product.Name,
            product.Description,
            product.IsActive,
            product.PriceBaseUsd,
            product.PriceBaseCop,
            ResolveTaxRateName(product.TaxRateId, taxRateNames),
            product.PriceScales
                .Select(scale => new ProductExportScale(scale.FromUnit, scale.ToUnit, scale.FinalCop))
                .ToList());

    /// <summary>`TaxRateId` es un struct nullable, asi que se desenvuelve antes de buscar: el
    /// diccionario esta tipado con el valor, no con el nullable.</summary>
    private static string? ResolveTaxRateName(
        TaxRateId? taxRateId, IReadOnlyDictionary<TaxRateId, string> names) =>
        taxRateId is { } id && names.TryGetValue(id, out var name) ? name : null;

    /// <summary>Con la fecha adentro: quien recibe varios correos necesita distinguirlos, y el
    /// nombre es lo unico que ve antes de abrir el archivo.</summary>
    private static string FileNameFor(DateTimeOffset now) =>
        $"productos-{now:yyyy-MM-dd-HHmm}.xlsx";
}
