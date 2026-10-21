using System.Globalization;
using BuildingBlocks.Application;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

/// <summary>
/// Exporta el catalogo del tenant a un Excel, lo deja en el almacenamiento de objetos y encola el
/// correo con el enlace de descarga. Gemelo de <c>ExportCustomersCommand</c>, con el mismo flujo.
///
/// Una fila por **variante** (spec 2026-10-20 §7), agrupadas por producto y en el orden de la
/// variante (`Position`). Se exportan tambien las variantes inactivas, marcadas "Inactivo": el
/// Excel es una foto del catalogo, no del surtido vendible, y el filtro `IsActive` ya es el que
/// decide si se ven los productos inactivos. La fila "Activo" es la que se puede vender: el
/// producto y la variante estan activos.
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
/// Lo que el request devuelve. `ProductCount` cuenta **productos**, no filas: es lo que el correo
/// dice ("N productos") y lo que el filtro dejo a la vista. **No lleva el enlace**: el contrato de esta operacion es que el
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
    IOptionDefinitionRepository optionRepository,
    IProductExportWorkbookBuilder exportBuilder,
    IProductExportStorage exportStorage,
    IProductExportEventPublisher exportEventPublisher,
    ICatalogAuditPublisher auditPublisher,
    ICatalogUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    ITenantClock tenantClock,
    ICurrenciesInUse currenciesInUse)
    : ICommandHandler<ExportProductsCommand, ExportProductsResult>
{
    /// <summary>
    /// Cuantos productos se traen por consulta. No es el tope de la exportacion: es el tamano del
    /// lote con el que se recorre, para no pedirle a PostgreSQL el catalogo entero de una.
    /// </summary>
    private const int BatchSize = 500;

    /// <summary>
    /// Tope duro de filas, que son **variantes** (un producto puede tener 2.048). El workbook se
    /// arma completo en memoria antes de subirse, asi que sin limite un tenant grande tumba el
    /// proceso en vez de devolver un error. Cuando alguien lo alcance, la respuesta correcta no es
    /// subirlo sino acotar la exportacion con los filtros.
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

        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var occurredAt = calendar.UtcNow;

        // Las tasas se resuelven de una sola vez para todo el lote: el producto guarda el id y la
        // planilla muestra el nombre, y pedirla producto por producto seria el N+1 clasico.
        var taxRates = await taxRateRepository.ListAsync(command.TenantId, cancellationToken);
        var taxRateNames = taxRates.ToDictionary(rate => rate.Id, rate => rate.Name);

        // Los nombres de los valores de opcion, tambien de una vez para todo el lote: el nombre
        // visible de una variante sale de la biblioteca, no del agregado.
        var valueNames = await LoadValueNamesAsync(products, command.TenantId, cancellationToken);

        var rows = products
            .SelectMany(product => ToRows(product, taxRateNames, valueNames))
            .ToList();
        var fileName = FileNameFor(calendar.ToLocal(occurredAt));

        // Only the currencies in use (spec D10): a column for a currency nobody prices in is noise.
        var currencies = (await currenciesInUse.GetAsync(command.TenantId, cancellationToken))
            .Select(Currencies.Get)
            .ToArray();
        var content = exportBuilder.Build(rows, currencies);

        // Antes de commitear: si la subida falla, la excepcion sube y no queda ni el evento ni la
        // entrada de auditoria. No hay exportacion a medias ni correo con un enlace que no resuelve.
        var upload = await exportStorage.UploadAsync(
            command.TenantId, fileName, content, cancellationToken);

        exportEventPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            upload.DownloadUrl,
            fileName,
            products.Count,
            upload.ExpiresAt,
            occurredAt);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "catalog.product.exported",
            fileName,
            $"success:{products.Count}",
            occurredAt);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new ExportProductsResult(fileName, products.Count, upload.ExpiresAt);
    }

    // Por lotes y no de una: SearchAsync pagina con un tope que existe para proteger la respuesta
    // HTTP, y este camino no devuelve las filas por HTTP.
    private async Task<IReadOnlyList<Product>> ReadAllAsync(
        ExportProductsCommand command,
        CancellationToken cancellationToken)
    {
        var all = new List<Product>();
        var rowCount = 0;
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
            rowCount += batch.Sum(product => product.Variants.Count);

            if (rowCount > MaxExportRows)
            {
                throw new CatalogDomainException(
                    "catalog.export.too_many_rows",
                    $"The export cannot exceed {MaxExportRows} rows (one per variant). Narrow it with filters.");
            }

            if (batch.Count < BatchSize)
            {
                return all;
            }
        }
    }

    /// <summary>Valor de opcion -> nombre, de las opciones de todos los productos del lote, en una
    /// sola consulta (ninguna si ninguno tiene opciones). Mismo criterio que el detalle: un valor
    /// cuya opcion ya no esta en la biblioteca no rompe la exportacion, el nombre visible lo omite.</summary>
    private async Task<IReadOnlyDictionary<Guid, string>> LoadValueNamesAsync(
        IReadOnlyList<Product> products, Guid tenantId, CancellationToken cancellationToken)
    {
        var optionIds = products
            .SelectMany(product => product.OptionIds)
            .Distinct()
            .Select(id => new OptionDefinitionId(id))
            .ToArray();
        if (optionIds.Length == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var options = await optionRepository.ListByIdsAsync(tenantId, optionIds, cancellationToken);
        return options
            .SelectMany(option => option.Values)
            .ToDictionary(value => value.Id, value => value.Value);
    }

    private static IEnumerable<ProductExportRow> ToRows(
        Product product,
        IReadOnlyDictionary<TaxRateId, string> taxRateNames,
        IReadOnlyDictionary<Guid, string> valueNames)
    {
        var taxRateName = ResolveTaxRateName(product.TaxRateId, taxRateNames);

        // `Variants` ya viene por `Position`: es el orden del formulario y el de la tabla.
        return product.Variants.Select(variant => new ProductExportRow(
            variant.Code,
            variant.Barcode,
            product.Name,
            // La variante por defecto no se distingue por opciones: su nombre ya esta en Producto.
            variant.OptionValueIds.Count == 0
                ? null
                : VariantDisplayName.For(
                    product.Name,
                    variant.OptionValueIds
                        .Where(valueNames.ContainsKey)
                        .Select(id => valueNames[id])
                        .ToArray()),
            product.Description,
            product.IsActive && variant.IsActive,
            variant.Prices.ToDictionary(price => price.Currency, price => price.Amount),
            taxRateName,
            variant.PriceScales
                .Select(scale => new ProductExportScale(scale.FromUnit, scale.ToUnit, scale.Discount))
                .ToList()));
    }

    /// <summary>`TaxRateId` es un struct nullable, asi que se desenvuelve antes de buscar: el
    /// diccionario esta tipado con el valor, no con el nullable.</summary>
    private static string? ResolveTaxRateName(
        TaxRateId? taxRateId, IReadOnlyDictionary<TaxRateId, string> names) =>
        taxRateId is { } id && names.TryGetValue(id, out var name) ? name : null;

    /// <summary>Con la fecha adentro: quien recibe varios correos necesita distinguirlos, y el
    /// nombre es lo unico que ve antes de abrir el archivo. En la hora del tenant (spec 2026-09-17,
    /// punto 8a): recibe el instante ya local y escribe su reloj.</summary>
    private static string FileNameFor(DateTimeOffset localNow) =>
        $"productos-{localNow.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture)}.xlsx";
}
