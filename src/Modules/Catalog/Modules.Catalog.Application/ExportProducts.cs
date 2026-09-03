using BuildingBlocks.Application;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

/// <summary>
/// Exporta el catalogo del tenant a un Excel, lo deja en el almacenamiento de objetos y encola el
/// correo con el enlace de descarga. Gemelo de <c>ExportCustomersCommand</c>, con el mismo flujo.
///
/// Los filtros son los mismos que <see cref="ListProductsQuery"/> a proposito: se exporta lo que se
/// esta viendo en la grilla. Sin ninguno, se exporta el catalogo entero. <c>Search</c> queda afuera:
/// ese criterio existe para el combobox de cotizaciones, no para el listado desde donde se exporta.
/// </summary>
public sealed record ExportProductsCommand(
    Guid TenantId,
    string? Name,
    string? Code,
    bool? IsActive) : ICommand<ExportProductsResult>;

/// <summary>
/// Lo que el request devuelve. **No lleva el enlace**: el contrato de esta operacion es que el
/// archivo llega por correo, y devolverlo tambien aca duplicaria el canal de entrega — el frontend
/// tomaria el atajo y el camino del correo quedaria sin ejercitar hasta que fallara en produccion.
/// </summary>
public sealed record ExportProductsResult(
    string FileName,
    int ProductCount,
    DateTimeOffset ExpiresAt);

public sealed class ExportProductsHandler(
    IProductRepository repository,
    ITaxRateRepository taxRateRepository,
    IProductExportWorkbookBuilder exportBuilder,
    IProductExportStorage exportStorage,
    IProductExportEventPublisher exportEventPublisher,
    ICatalogAuditPublisher auditPublisher,
    ICatalogUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ExportProductsCommand, ExportProductsResult>
{
    /// <summary>
    /// Cuantos productos se traen por consulta. No es el tope de la exportacion: es el tamano del
    /// lote con el que se recorre, para no pedirle a PostgreSQL el catalogo entero de una.
    /// </summary>
    private const int BatchSize = 500;

    /// <summary>
    /// Tope duro de filas. El workbook se arma completo en memoria antes de subirse, asi que sin
    /// limite un tenant grande tumba el proceso en vez de devolver un error. Cuando alguien lo
    /// alcance, la respuesta correcta no es subirlo sino acotar la exportacion con los filtros.
    /// </summary>
    private const int MaxExportRows = 20_000;

    public async Task<ExportProductsResult> HandleAsync(
        ExportProductsCommand command,
        CancellationToken cancellationToken)
    {
        CatalogAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CatalogPermissions.ProductRead);

        var products = await ReadAllAsync(command, cancellationToken);
        if (products.Count == 0)
        {
            // Un archivo con solo la cabecera, o un correo con un Excel vacio, es peor que decir
            // que no habia nada para exportar.
            throw new CatalogDomainException(
                "catalog.export.empty",
                "There are no products matching the export criteria.");
        }

        var occurredAt = clock.UtcNow;

        // Las tasas se resuelven de una sola vez para todo el lote: el producto guarda el id y la
        // planilla muestra el nombre, y pedirla producto por producto seria el N+1 clasico.
        var taxRates = await taxRateRepository.ListAsync(command.TenantId, cancellationToken);
        var taxRateNames = taxRates.ToDictionary(rate => rate.Id, rate => rate.Name);

        var rows = products.Select(product => ToRow(product, taxRateNames)).ToList();
        var fileName = FileNameFor(occurredAt);
        var content = exportBuilder.Build(rows);

        // Antes de commitear: si la subida falla, la excepcion sube y no queda ni el evento ni la
        // entrada de auditoria. No hay exportacion a medias ni correo con un enlace que no resuelve.
        var upload = await exportStorage.UploadAsync(
            command.TenantId, fileName, content, cancellationToken);

        exportEventPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            upload.DownloadUrl,
            fileName,
            rows.Count,
            upload.ExpiresAt,
            occurredAt);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "catalog.product.exported",
            fileName,
            $"success:{rows.Count}",
            occurredAt);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new ExportProductsResult(fileName, rows.Count, upload.ExpiresAt);
    }

    // Por lotes y no de una: SearchAsync pagina con un tope que existe para proteger la respuesta
    // HTTP, y este camino no devuelve las filas por HTTP.
    private async Task<IReadOnlyList<Product>> ReadAllAsync(
        ExportProductsCommand command,
        CancellationToken cancellationToken)
    {
        var all = new List<Product>();
        while (true)
        {
            var batch = await repository.ListForExportAsync(
                command.TenantId,
                command.Name,
                command.Code,
                command.IsActive,
                all.Count,
                BatchSize,
                cancellationToken);

            all.AddRange(batch);

            if (all.Count > MaxExportRows)
            {
                throw new CatalogDomainException(
                    "catalog.export.too_many_rows",
                    $"The export cannot exceed {MaxExportRows} products. Narrow it with filters.");
            }

            if (batch.Count < BatchSize)
            {
                return all;
            }
        }
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
