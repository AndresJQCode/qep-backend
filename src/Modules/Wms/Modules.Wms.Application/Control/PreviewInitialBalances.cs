using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Control;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Application;

public sealed record InitialBalancePreviewRowDto(
    int Row,
    string Code,
    Guid? VariantId,
    string? ProductName,
    string LocationCode,
    Guid? LocationId,
    string? LotCode,
    DateOnly? ExpirationDate,
    decimal Quantity,
    string? Uom,
    IReadOnlyList<string> Issues);

public sealed record InitialBalancePreviewDto(
    Guid PreviewId,
    DateTimeOffset ExpiresAt,
    long WarehouseVersion,
    IReadOnlyList<InitialBalancePreviewRowDto> Rows,
    bool CanCommit);

/// <summary>Lo que el preview guarda por fila: la fila que ve la persona más la cantidad que escribió en su presentación, para armar la
/// captura del movimiento al commitear sin volver a leer el perfil. Es interno del preview y no sale por la API.</summary>
public sealed record InitialBalanceStoredRow(InitialBalancePreviewRowDto Row, decimal? CapturedQuantity);

public static class InitialBalanceJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

public sealed record PreviewInitialBalancesCommand(
    Guid TenantId, Guid WarehouseId, Guid? OwnerId, string FileName, long Length, Stream Content) : ICommand<InitialBalancePreviewDto>;

/// <summary>
/// RF-024, primera mitad: lee la hoja, resuelve cada fila contra Catalog, la bodega y el perfil, y guarda el resultado hasta que alguien
/// lo confirme. Los problemas son datos de la fila y no un error del request (S21): un preview con issues responde 200 con
/// <c>canCommit = false</c>. Sólo un archivo inservible o de más de 500 filas se rechaza aquí.
/// </summary>
public sealed class PreviewInitialBalancesHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IWmsProvisioner provisioner,
    IWmsUnitOfWork unitOfWork,
    IClock clock,
    IOwnerRepository owners,
    IWarehouseRepository warehouses,
    IVariantProfileRepository profiles,
    ILotRepository lots,
    IWmsCatalogReader catalog,
    IInitialBalanceSheetReader reader,
    IImportPreviewStore store)
    : ICommandHandler<PreviewInitialBalancesCommand, InitialBalancePreviewDto>
{
    public const long MaxFileBytes = 5 * 1024 * 1024;

    public async Task<InitialBalancePreviewDto> HandleAsync(PreviewInitialBalancesCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await authorization.EnsureAsync(
            executionContext, command.TenantId, WmsPermissions.AdjustmentPropose, warehouseId: command.WarehouseId, ct: cancellationToken);

        if (command.Length is <= 0 or > MaxFileBytes)
        {
            throw new WmsDomainException(WmsErrorCodes.ImportInvalidFile, "El archivo está vacío o pesa más de 5 MB.");
        }

        // El Self lo escribe sólo el provisioner (S5). Este flujo no abre transacción: la única escritura es la fila del preview.
        if (command.OwnerId is null)
        {
            await provisioner.EnsureAsync(command.TenantId, cancellationToken);
        }

        var ownerId = await ResolveOwnerAsync(command, cancellationToken);
        await authorization.EnsureAsync(
            executionContext, command.TenantId, WmsPermissions.AdjustmentPropose, ownerId, command.WarehouseId, cancellationToken);
        var warehouseId = new WarehouseId(command.WarehouseId);
        var warehouse = await warehouses.GetAsync(command.TenantId, warehouseId, cancellationToken)
            ?? throw WmsNotFound.Warehouse(command.WarehouseId);

        var sheet = reader.Parse(command.Content, cancellationToken);
        if (!sheet.HasExpectedColumns || sheet.Rows.Count == 0)
        {
            throw new WmsDomainException(
                WmsErrorCodes.ImportInvalidFile,
                "El archivo no tiene las columnas code, location, lot, expiration, quantity y uom, o no trae filas de datos.");
        }

        // La carga es un solo batch del ledger y cada fila es un movimiento: más de 500 nunca podría commitearse (S21).
        if (sheet.Rows.Count > LedgerBatch.MaxEntries)
        {
            throw new WmsDomainException(
                WmsErrorCodes.ImportTooManyRows, $"La hoja tiene más de {LedgerBatch.MaxEntries} filas; divídela en varias cargas.");
        }

        var rows = await ClassifyAsync(command.TenantId, ownerId, warehouseId, sheet.Rows, cancellationToken);
        var canCommit = rows.All(stored => stored.Row.Issues.Count == 0);
        var preview = ImportPreview.Create(
            command.TenantId,
            executionContext.SubjectId,
            warehouseId,
            new OwnerId(ownerId),
            JsonSerializer.Serialize(rows, InitialBalanceJson.Options),
            canCommit,
            clock.UtcNow,
            store.Lifetime);
        store.Add(preview);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new InitialBalancePreviewDto(preview.Id, preview.ExpiresAt, warehouse.Version, [.. rows.Select(stored => stored.Row)], canCommit);
    }

    private async Task<Guid> ResolveOwnerAsync(PreviewInitialBalancesCommand command, CancellationToken cancellationToken)
    {
        if (command.OwnerId is not { } requested)
        {
            return (await owners.GetSelfAsync(command.TenantId, cancellationToken)).Id.Value;
        }

        var owner = await owners.FindAsync(command.TenantId, new OwnerId(requested), cancellationToken)
            ?? throw WmsNotFound.Owner(requested);
        return owner.Id.Value;
    }

    private async Task<List<InitialBalanceStoredRow>> ClassifyAsync(
        Guid tenantId, Guid ownerId, WarehouseId warehouseId, IReadOnlyList<InitialBalanceSheetRow> sheetRows, CancellationToken cancellationToken)
    {
        var codes = sheetRows
            .Select(row => row.Code?.Trim())
            .Where(code => !string.IsNullOrEmpty(code))
            .Select(code => code!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var candidates = codes.Count == 0
            ? Enumerable.Empty<WmsVariantSnapshot>().ToLookup(snapshot => snapshot.Code)
            : (await catalog.FindVariantsByCodesAsync(tenantId, codes, cancellationToken)).ToLookup(snapshot => snapshot.Code, StringComparer.OrdinalIgnoreCase);

        var locations = new Dictionary<string, Location?>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in sheetRows.Select(row => row.Location?.Trim()).Where(code => !string.IsNullOrEmpty(code)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            locations[code!] = await warehouses.FindLocationByCodeAsync(tenantId, warehouseId, code!, cancellationToken);
        }

        var variantIds = codes
            .SelectMany(code => candidates[code])
            .Select(snapshot => snapshot.VariantId)
            .Distinct()
            .ToList();
        var variantProfiles = variantIds.Count == 0
            ? new Dictionary<Guid, VariantLogisticsProfile>()
            : (await profiles.ListAsync(tenantId, variantIds, cancellationToken)).ToDictionary(profile => profile.VariantId);

        var seen = new HashSet<(Guid VariantId, Guid LocationId, string? LotCode)>();
        var result = new List<InitialBalanceStoredRow>(sheetRows.Count);
        foreach (var row in sheetRows)
        {
            var code = row.Code?.Trim() ?? string.Empty;
            var rowCandidates = candidates[code].ToList();
            var resolved = InitialBalanceRowRules.FindExact(code, rowCandidates);
            var profile = resolved is null ? null : variantProfiles.GetValueOrDefault(resolved.VariantId);
            var rules = resolved is null ? null : RulesFor(resolved, profile);
            var location = row.Location is null ? null : locations.GetValueOrDefault(row.Location.Trim());

            var tracksExpiry = await NeedsExpirationAsync(tenantId, ownerId, resolved, profile, rules, row, cancellationToken);
            var classified = InitialBalanceRowRules.ClassifyDetailed(row, rowCandidates, location, rules, tracksExpiry, seen);
            result.Add(new InitialBalanceStoredRow(classified.Row, classified.CapturedQuantity));
        }

        return result;
    }

    // El perfil por defecto nace al primer movimiento, no en el preview (un preview no escribe): aquí se arma en memoria con los mismos
    // valores que VariantLogisticsProfile.CreateDefault, para que la fila se juzgue como la va a juzgar el ledger.
    internal static LedgerVariantRules RulesFor(WmsVariantSnapshot variant, VariantLogisticsProfile? profile)
    {
        if (profile is not null)
        {
            return new LedgerVariantRules(
                profile.BaseUom.Value,
                profile.BaseUomPrecision,
                profile.Tracking,
                profile.Conversions.ToDictionary(conversion => conversion.Code, conversion => conversion.Factor, StringComparer.OrdinalIgnoreCase));
        }

        var factors = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var units in variant.PackagingUnits.Where(units => units > 0).Distinct())
        {
            factors[$"EMPAQUE x{units}"] = units;
        }

        return new LedgerVariantRules("UND", 0, TrackingPolicy.None, factors);
    }

    // Un lote que ya existe conserva su fecha de vencimiento: pedírsela de nuevo a la fila sería rechazar una hoja correcta.
    private async Task<bool> NeedsExpirationAsync(
        Guid tenantId,
        Guid ownerId,
        WmsVariantSnapshot? variant,
        VariantLogisticsProfile? profile,
        LedgerVariantRules? rules,
        InitialBalanceSheetRow row,
        CancellationToken cancellationToken)
    {
        if (variant is null || profile?.TracksExpiry != true || rules?.Tracking != TrackingPolicy.Lot || string.IsNullOrWhiteSpace(row.Lot))
        {
            return false;
        }

        return await lots.FindAsync(tenantId, ownerId, variant.VariantId, row.Lot.Trim(), cancellationToken) is null;
    }
}
