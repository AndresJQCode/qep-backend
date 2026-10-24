using BuildingBlocks.Application;
using Modules.Suppliers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Suppliers.Application;

/// <summary>
/// Exporta el directorio de proveedores del tenant a un Excel, lo deja en el almacenamiento de
/// objetos y encola el correo con el enlace de descarga. Los filtros son los de
/// <see cref="ListSuppliersQuery"/> a propósito: se exporta lo que se está viendo en la grilla.
/// </summary>
public sealed record ExportSuppliersCommand(
    Guid TenantId,
    string? Search,
    string? Status,
    string? Category) : ICommand<ExportSuppliersResult>;

/// <summary>
/// Lo que el request devuelve. **No lleva el enlace**: el archivo llega por correo, y devolverlo
/// también aquí duplicaría el canal de entrega.
/// </summary>
public sealed record ExportSuppliersResult(
    string FileName,
    int SupplierCount,
    DateTimeOffset ExpiresAt);

public sealed class ExportSuppliersHandler(
    ISupplierRepository repository,
    ISupplierGeographyLookup geographyLookup,
    ISupplierExportBuilder exportBuilder,
    ISupplierExportStorage exportStorage,
    ISupplierExportEventPublisher exportEventPublisher,
    ISuppliersAuditPublisher auditPublisher,
    ISuppliersUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    ITenantClock tenantClock)
    : ICommandHandler<ExportSuppliersCommand, ExportSuppliersResult>
{
    /// <summary>El tamaño del lote con el que se recorre, no el tope de la exportación.</summary>
    private const int BatchSize = 500;

    /// <summary>
    /// Tope duro: el workbook se arma completo en memoria, y sin límite un tenant grande tumba el
    /// proceso en vez de devolver un error. La salida es acotar con filtros, no subirlo.
    /// </summary>
    private const int MaxExportRows = 50_000;

    public async Task<ExportSuppliersResult> HandleAsync(
        ExportSuppliersCommand command,
        CancellationToken cancellationToken)
    {
        // Antes de tocar ningún puerto: el aislamiento de tenant no depende de la política de ruta.
        SuppliersAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, SuppliersPermissions.Read);

        var status = SupplierStatusFilterParser.Parse(command.Status);
        var suppliers = await ReadAllAsync(command, status, cancellationToken);
        if (suppliers.Count == 0)
        {
            // Un Excel con sólo la cabecera es peor que decir que no había nada que exportar.
            throw new SuppliersDomainException(
                "suppliers.export.empty",
                "There are no suppliers matching the export criteria.");
        }

        // El archivo se nombra en la hora del tenant; auditoría y evento siguen en UTC.
        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var occurredAt = calendar.UtcNow;
        var items = await ToResponsesAsync(suppliers, cancellationToken);
        var file = exportBuilder.Build(items, calendar, cancellationToken);

        // Antes de commitear: si la subida falla, la excepción sube y no queda ni el evento ni la
        // auditoría. No hay exportación a medias ni correo con un enlace que no resuelve.
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
            SupplierAuditActions.Exported,
            file.FileName,
            $"success:{items.Count}",
            occurredAt);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new ExportSuppliersResult(file.FileName, items.Count, upload.ExpiresAt);
    }

    private async Task<IReadOnlyList<Supplier>> ReadAllAsync(
        ExportSuppliersCommand command,
        SupplierStatusFilter? status,
        CancellationToken cancellationToken)
    {
        var all = new List<Supplier>();
        while (true)
        {
            var batch = await repository.ListForExportAsync(
                command.TenantId,
                command.Search,
                status,
                command.Category,
                all.Count,
                BatchSize,
                cancellationToken);

            all.AddRange(batch);

            if (all.Count > MaxExportRows)
            {
                throw new SuppliersDomainException(
                    "suppliers.export.too_many_rows",
                    $"The export cannot exceed {MaxExportRows} suppliers. Narrow it with filters.");
            }

            if (batch.Count < BatchSize)
            {
                return all;
            }
        }
    }

    // Las ciudades de todo el tenant en una consulta en lote, como la lista: a esta escala una
    // consulta por proveedor serían decenas de miles de round-trips.
    private async Task<IReadOnlyList<SupplierResponse>> ToResponsesAsync(
        IReadOnlyList<Supplier> suppliers,
        CancellationToken cancellationToken)
    {
        var cityIds = suppliers
            .Where(supplier => supplier.CityId is not null)
            .Select(supplier => supplier.CityId!.Value)
            .Distinct()
            .ToArray();
        var citiesById = cityIds.Length == 0
            ? new Dictionary<Guid, SupplierCityRef>()
            : await geographyLookup.FindCitiesAsync(cityIds, cancellationToken);

        return [.. suppliers.Select(supplier => supplier.ToResponse(ResolveCity(citiesById, supplier)))];
    }

    // Un miss es corrupción de datos y no una entrada inválida: mismo criterio que la lista.
    private static SupplierCityRef? ResolveCity(
        IReadOnlyDictionary<Guid, SupplierCityRef> citiesById,
        Supplier supplier) =>
        supplier.CityId is not { } cityId
            ? null
            : citiesById.TryGetValue(cityId, out var city)
                ? city
                : throw new InvalidOperationException(
                    $"City '{cityId}' referenced by supplier '{supplier.Id}' was not found.");
}
