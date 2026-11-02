using System.Globalization;
using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Inventory;
using static Modules.Wms.Application.WmsAuditActions;

namespace Modules.Wms.Application;

/// <summary>Los filtros de <see cref="GetStockQuery"/> sin la paginación: se exporta lo que se está viendo en la grilla, completo.</summary>
public sealed record ExportStockRequest(
    Guid? WarehouseId,
    Guid? OwnerId,
    Guid? VariantId,
    Guid? LocationId,
    Guid? LotId,
    string? Status,
    bool? OnlyAvailable,
    bool? IncludeVirtual,
    string? Search);

public sealed record ExportStockCommand(
    Guid TenantId,
    Guid? WarehouseId,
    Guid? OwnerId,
    Guid? VariantId,
    Guid? LocationId,
    Guid? LotId,
    string? Status,
    bool OnlyAvailable,
    bool IncludeVirtual,
    string? Search) : ICommand<StockExportAcceptedDto>
{
    public static ExportStockCommand From(Guid tenantId, ExportStockRequest? request)
    {
        request ??= new ExportStockRequest(null, null, null, null, null, null, null, null, null);

        return new(
            tenantId,
            request.WarehouseId,
            request.OwnerId,
            request.VariantId,
            request.LocationId,
            request.LotId,
            request.Status,
            request.OnlyAvailable is true,
            request.IncludeVirtual is true,
            request.Search);
    }
}

/// <summary>Lo que el request devuelve. No lleva el enlace: el archivo llega por correo, y devolverlo también aquí duplicaría el canal.</summary>
public sealed record StockExportAcceptedDto(Guid FileId, string FileName, int RowCount, DateTimeOffset ExpiresAt);

/// <summary>
/// RF-030, como la exportación de Suppliers: el Excel se arma en el request (tope de <see cref="MaxExportRows"/> filas), se sube y se
/// publica <c>wms.export-ready.v1</c> —nunca el de proveedores, S15— para que <c>WmsExportDeliveryWorker</c> mande el enlace por correo.
/// Si 50 000 filas pasan de los segundos de un request, el camino de upgrade es un job encolado como el de Quotations.
/// </summary>
public sealed class ExportStockHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IStockReadQueries queries,
    IWmsCatalogReader catalog,
    IVariantProfileRepository profiles,
    IStockExportBuilder builder,
    IWmsExportStorage storage,
    IWmsEventPublisher events,
    IWmsAuditRecorder auditRecorder,
    IWmsUnitOfWork unitOfWork,
    ITenantClock tenantClock)
    : ICommandHandler<ExportStockCommand, StockExportAcceptedDto>
{
    /// <summary>El tamaño del lote con el que se recorre, no el tope de la exportación.</summary>
    private const int BatchSize = 500;

    /// <summary>Tope duro: el workbook se arma completo en memoria, y sin límite un tenant grande tumba el proceso en vez de devolver un
    /// error. La salida es acotar con filtros, no subirlo.</summary>
    public const int MaxExportRows = 50_000;

    public async Task<StockExportAcceptedDto> HandleAsync(ExportStockCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await authorization.EnsureAsync(
            executionContext, command.TenantId, WmsPermissions.StockExport, command.OwnerId, command.WarehouseId, cancellationToken);

        var status = StockStatuses.ParseOrNull(command.Status);
        var search = string.IsNullOrWhiteSpace(command.Search) ? null : command.Search.Trim();
        var rows = await ReadAllAsync(command, status, search, cancellationToken);
        if (rows.Count == 0)
        {
            // Un Excel con sólo la cabecera es peor que decir que no había nada que exportar.
            throw new WmsDomainException(WmsErrorCodes.ExportEmpty, "No hay saldos que coincidan con el filtro de la exportación.");
        }

        // El archivo se nombra en la hora del tenant; el evento y la auditoría siguen en UTC.
        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var occurredAt = calendar.UtcNow;
        var file = builder.Build(rows, calendar, cancellationToken);

        // Antes de commitear: si la subida falla, la excepción sube y no queda ni el evento ni la auditoría. No hay exportación a medias
        // ni correo con un enlace que no resuelve.
        var fileId = Guid.CreateVersion7();
        var upload = await storage.UploadAsync(command.TenantId, fileId, file.FileName, file.Content, cancellationToken);

        events.PublishExportReady(
            command.TenantId, executionContext.SubjectId, fileId, upload.DownloadUrl, file.FileName, rows.Count, upload.ExpiresAt, occurredAt);
        auditRecorder.Record(
            command.TenantId,
            executionContext.SubjectId,
            WmsActorType.Human,
            StockExported,
            Resources.Export,
            fileId.ToString(),
            new Dictionary<string, string?>
            {
                ["ownerId"] = command.OwnerId?.ToString(),
                ["rowCount"] = rows.Count.ToString(CultureInfo.InvariantCulture),
                ["fileName"] = file.FileName,
            },
            occurredAt);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new StockExportAcceptedDto(fileId, file.FileName, rows.Count, upload.ExpiresAt);
    }

    private async Task<List<StockRowDto>> ReadAllAsync(
        ExportStockCommand command, StockStatus? status, string? search, CancellationToken cancellationToken)
    {
        var all = new List<StockRowDto>();
        while (true)
        {
            var filter = new StockFilter(
                command.TenantId,
                command.WarehouseId,
                command.OwnerId,
                command.VariantId,
                command.LocationId,
                command.LotId,
                status,
                command.OnlyAvailable,
                command.IncludeVirtual,
                search,
                all.Count,
                BatchSize);
            var batch = await queries.SearchAsync(filter, cancellationToken);

            // El total sale de la misma consulta: se corta antes de bajar decenas de miles de filas que no se van a exportar.
            if (batch.Total > MaxExportRows)
            {
                throw new WmsDomainException(
                    WmsErrorCodes.ExportTooManyRows, $"La exportación no puede pasar de {MaxExportRows} filas. Acótala con filtros.");
            }

            all.AddRange(await ComposeAsync(command.TenantId, batch.Rows, cancellationToken));
            if (batch.Rows.Count < BatchSize || all.Count >= batch.Total)
            {
                return all;
            }
        }
    }

    private async Task<IEnumerable<StockRowDto>> ComposeAsync(
        Guid tenantId, IReadOnlyList<StockBalanceRow> rows, CancellationToken cancellationToken)
    {
        var variantIds = rows.Select(row => row.VariantId).Distinct().ToList();
        var snapshots = (await catalog.GetVariantsAsync(tenantId, variantIds, cancellationToken)).ToDictionary(snapshot => snapshot.VariantId);
        var variantProfiles = (await profiles.ListAsync(tenantId, variantIds, cancellationToken)).ToDictionary(profile => profile.VariantId);

        return rows.Select(row => StockRowMapper.ToDto(
            row, snapshots.GetValueOrDefault(row.VariantId), variantProfiles.GetValueOrDefault(row.VariantId)));
    }
}
