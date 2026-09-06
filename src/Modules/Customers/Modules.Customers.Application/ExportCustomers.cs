using BuildingBlocks.Application;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

/// <summary>
/// Exporta el padron de clientes del tenant a un Excel, lo deja en el almacenamiento de objetos y
/// encola el correo con el enlace de descarga.
///
/// Los filtros son los mismos que <see cref="ListCustomersQuery"/> a proposito: se exporta lo que
/// se esta viendo en la grilla. Sin ninguno, se exporta el tenant entero.
/// </summary>
public sealed record ExportCustomersCommand(
    Guid TenantId,
    string? Search,
    string? Name,
    string? IdentificationNumber,
    string? Cuc) : ICommand<ExportCustomersResult>;

/// <summary>
/// Lo que el request devuelve. **No lleva el enlace**: el contrato de esta operacion es que el
/// archivo llega por correo, y devolverlo tambien aca duplicaria el canal de entrega — el frontend
/// tomaria el atajo y el camino del correo quedaria sin ejercitar hasta que fallara en produccion.
/// </summary>
public sealed record ExportCustomersResult(
    string FileName,
    int CustomerCount,
    DateTimeOffset ExpiresAt);

public sealed class ExportCustomersHandler(
    ICustomerRepository repository,
    IClientClassificationRepository classificationRepository,
    ICustomerGeographyLookup geographyLookup,
    ICustomerExportBuilder exportBuilder,
    ICustomerExportStorage exportStorage,
    ICustomerExportEventPublisher exportEventPublisher,
    ICustomersAuditPublisher auditPublisher,
    ICustomersUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ExportCustomersCommand, ExportCustomersResult>
{
    /// <summary>
    /// Cuantos clientes se traen por consulta. No es el tope de la exportacion: es el tamano del
    /// lote con el que se recorre, para no pedirle a PostgreSQL el tenant entero de una.
    /// </summary>
    private const int BatchSize = 500;

    /// <summary>
    /// Tope duro de filas. El workbook se arma completo en memoria antes de subirse, asi que sin
    /// limite un tenant grande tumba el proceso en vez de devolver un error. Cuando alguien lo
    /// alcance, la respuesta correcta no es subirlo sino acotar la exportacion con los filtros.
    /// </summary>
    private const int MaxExportRows = 50_000;

    public async Task<ExportCustomersResult> HandleAsync(
        ExportCustomersCommand command,
        CancellationToken cancellationToken)
    {
        CustomersAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CustomersPermissions.CustomerRead);

        var customers = await ReadAllAsync(command, cancellationToken);
        if (customers.Count == 0)
        {
            // Mismo criterio que el export de filas fallidas: un archivo con solo la cabecera, o un
            // correo con un Excel vacio, es peor que decir que no habia nada para exportar.
            throw new CustomersDomainException(
                "customers.export.empty",
                "There are no customers matching the export criteria.");
        }

        var occurredAt = clock.UtcNow;
        var items = await ToDtosAsync(command.TenantId, customers, cancellationToken);
        var file = exportBuilder.Build(items, occurredAt, cancellationToken);

        // Antes de commitear: si la subida falla, la excepcion sube y no queda ni el evento ni la
        // entrada de auditoria. No hay exportacion a medias ni correo con un enlace que no resuelve.
        var upload = await exportStorage.UploadAsync(
            command.TenantId, file.FileName, file.Content, cancellationToken);

        exportEventPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            upload.DownloadUrl,
            file.FileName,
            items.Count,
            upload.ExpiresAt,
            occurredAt);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "customers.customer.exported",
            file.FileName,
            $"success:{items.Count}",
            occurredAt);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new ExportCustomersResult(file.FileName, items.Count, upload.ExpiresAt);
    }

    // Por lotes y no de una: SearchAsync pagina con un tope de 200 (CustomerPaging.MaxPageSize) que
    // existe para proteger la respuesta HTTP, y este camino no devuelve las filas por HTTP.
    private async Task<IReadOnlyList<Customer>> ReadAllAsync(
        ExportCustomersCommand command,
        CancellationToken cancellationToken)
    {
        var all = new List<Customer>();
        while (true)
        {
            var batch = await repository.ListForExportAsync(
                command.TenantId,
                command.Search,
                command.Name,
                command.IdentificationNumber,
                command.Cuc,
                all.Count,
                BatchSize,
                cancellationToken);

            all.AddRange(batch);

            if (all.Count > MaxExportRows)
            {
                throw new CustomersDomainException(
                    "customers.export.too_many_rows",
                    $"The export cannot exceed {MaxExportRows} customers. Narrow it with filters.");
            }

            if (batch.Count < BatchSize)
            {
                return all;
            }
        }
    }

    // La misma resolucion en lote que ListCustomersHandler, y aca importa mas: a escala de tenant
    // completo, una consulta por cliente serian decenas de miles de round-trips.
    private async Task<IReadOnlyList<CustomerDto>> ToDtosAsync(
        Guid tenantId,
        IReadOnlyList<Customer> customers,
        CancellationToken cancellationToken)
    {
        var cityIds = customers
            .SelectMany(customer => customer.Addresses.Select(address => address.CityId))
            .Distinct()
            .ToArray();
        var classificationIds = customers
            .Select(customer => customer.ClassificationId)
            .Distinct()
            .ToArray();

        var citiesById = await geographyLookup.FindCitiesAsync(cityIds, cancellationToken);
        var classifications = await classificationRepository.ListByIdsAsync(
            tenantId, classificationIds, cancellationToken);
        var classificationsById = classifications.ToDictionary(
            classification => classification.Id);

        var items = new List<CustomerDto>(customers.Count);
        foreach (var customer in customers)
        {
            // La FK de base garantiza las dos referencias: un miss aca es corrupcion de datos, no
            // entrada de usuario invalida. Mismo criterio que ListCustomersHandler.
            var city = citiesById.TryGetValue(customer.RequirePrincipalAddress().CityId, out var cityRef)
                ? cityRef
                : throw new InvalidOperationException(
                    $"City '{customer.RequirePrincipalAddress().CityId}' referenced by customer '{customer.Id}' " +
                    "was not found.");
            var classification = classificationsById.TryGetValue(
                customer.ClassificationId, out var classificationValue)
                ? classificationValue
                : throw new InvalidOperationException(
                    $"Classification '{customer.ClassificationId}' referenced by customer " +
                    $"'{customer.Id}' was not found.");

            items.Add(customer.ToDto(city, classification, citiesById));
        }

        return items;
    }
}
