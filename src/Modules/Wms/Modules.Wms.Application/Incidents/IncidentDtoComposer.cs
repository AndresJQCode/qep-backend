using Modules.Wms.Domain.Control;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Application;

/// <summary>Lo que se mira, fuera de la novedad, para decidir si <c>AcceptIntoStock</c> tiene algo seguro que aplicar: el destino de la
/// línea de origen, lo que le falta por confirmar (nulo si la línea ya no se encuentra) y si la variante se controla por lote.</summary>
internal sealed record AcceptIntoStockFacts(Guid? LineDestination, decimal? LineRemaining, bool VariantIsLotTracked)
{
    public static AcceptIntoStockFacts None { get; } = new(null, null, false);
}

/// <summary>Qué resoluciones ofrece una novedad concreta y cuándo <c>AcceptIntoStock</c> tiene algo que aplicar (decisión 8 del plan).
/// La usan la ficha (para pintar los botones) y <c>resolve</c> (para no aceptar lo que la ficha no ofrece).</summary>
internal static class IncidentResolutionRules
{
    public static bool IsClosed(Incident incident) => incident.Status is IncidentStatus.Resolved or IncidentStatus.Rejected;

    /// <summary>Un conflicto con algo encontrado (<c>ActualQty &gt; 0</c>) y una línea de origen con destino: el ledger recibe un
    /// movimiento <c>origen → destino</c> de esa cantidad. Sin eso sólo se rechaza. Tampoco se acepta (revisión final de T2) si la acción
    /// rechazada apuntó a otro destino (<c>toLocationId</c> del cuerpo crudo distinto del de la línea: se movió a otro lado), si la
    /// variante se controla por lote y la novedad no trae lote (el lote que viaja en el cuerpo no se usa: la FK no mira el tenant), ni
    /// si lo encontrado supera lo que todavía le falta a la línea.</summary>
    public static bool CanAcceptIntoStock(Incident incident, AcceptIntoStockFacts facts)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentNullException.ThrowIfNull(facts);
        return incident.Type == IncidentType.SyncConflict
            && incident.ActualQty is > 0
            && incident.SourceTaskLineId is not null
            && incident.LocationId is not null
            && incident.VariantId is not null
            && incident.StockStatus is not null
            && facts.LineDestination is { } destination
            && !PayloadTargetsAnotherDestination(incident.RawPayload, destination)
            && (!facts.VariantIsLotTracked || incident.LotId is not null)
            && facts.LineRemaining is { } remaining
            && incident.ActualQty <= remaining;
    }

    // El cuerpo crudo de un confirm trae toLocationId; el de short o count no. Un valor que no se puede leer cuenta como otro destino:
    // no hay forma de saber a dónde se movió.
    private static bool PayloadTargetsAnotherDestination(string? rawPayload, Guid lineDestination)
    {
        if (string.IsNullOrWhiteSpace(rawPayload))
        {
            return false;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(rawPayload);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                || !document.RootElement.TryGetProperty("toLocationId", out var target)
                || target.ValueKind == System.Text.Json.JsonValueKind.Null)
            {
                return false;
            }

            return target.ValueKind != System.Text.Json.JsonValueKind.String
                || !Guid.TryParse(target.GetString(), out var parsed)
                || parsed != lineDestination;
        }
        catch (System.Text.Json.JsonException)
        {
            return true;
        }
    }

    public static IReadOnlyList<string> Allowed(Incident incident, AcceptIntoStockFacts facts)
    {
        ArgumentNullException.ThrowIfNull(incident);
        return IsClosed(incident)
            ? []
            : [.. IncidentCatalog.ResolutionsOf(incident.Type)
                .Where(resolution => resolution != IncidentResolution.AcceptIntoStock || CanAcceptIntoStock(incident, facts))
                .Select(resolution => resolution.ToString())
                .Order(StringComparer.Ordinal)];
    }

    public static bool CanBeRejected(Incident incident) =>
        !IsClosed(incident) && IncidentCatalog.CanBeRejected(incident.Type);
}

/// <summary>Arma los DTO de novedades: códigos de ubicación, variante y producto, código de lote, número de la tarea de origen y
/// nombres de las personas salen del servidor (como <see cref="WarehouseTaskDtoComposer"/>). Las lecturas van a la base: lo que la
/// transacción acaba de crear tiene que estar enviado (<see cref="IWmsUnitOfWork.FlushAsync"/>) antes de componer.</summary>
public sealed class IncidentDtoComposer(
    IWarehouseRepository warehouses,
    IWmsCatalogReader catalog,
    ILotRepository lots,
    IWmsMemberDirectory members,
    IWarehouseTaskRepository tasks,
    IVariantProfileRepository profiles)
{
    private const string UnknownName = "Sin nombre";
    private const string SystemName = "Sistema";

    private sealed record Lookups(
        IReadOnlyDictionary<LocationId, string> LocationCodes,
        IReadOnlyDictionary<Guid, WmsVariantSnapshot> Variants,
        IReadOnlyDictionary<Guid, string> TaskNumbers,
        IReadOnlyDictionary<Guid, string> Names);

    public async Task<IncidentDto> ComposeAsync(Incident incident, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incident);

        // La historia sigue el orden del servidor (RecordedAt); los hechos de una transacción comparten fecha y un UUID v7 no ordena
        // dentro del mismo milisegundo: dentro de una fecha manda el tipo, que el enum declara en orden causal.
        var history = incident.Events.OrderBy(value => value.RecordedAt).ThenBy(value => value.Kind).ThenBy(value => value.Id).ToList();
        var extraMembers = history.Where(value => value.MemberId is not null).Select(value => value.MemberId!.Value)
            .Concat(incident.ResolvedBy is { } resolver ? [resolver] : []);
        var lookups = await LoadAsync(incident.TenantId, [incident], extraMembers, cancellationToken);
        var summary = ToSummary(incident, lookups);

        var lotCode = incident.LotId is { } lotId
            ? (await lots.GetManyAsync(incident.TenantId, [lotId.Value], cancellationToken)).GetValueOrDefault(lotId.Value)?.Code
            : null;
        var facts = AcceptIntoStockFacts.None;
        if (incident.SourceTaskLineId is { } lineId
            && incident.Type == IncidentType.SyncConflict
            && !IncidentResolutionRules.IsClosed(incident))
        {
            facts = await ReadFactsAsync(tasks, profiles, incident, lineId, cancellationToken);
        }

        return new IncidentDto(
            summary.Id,
            summary.Number,
            summary.Type,
            summary.Status,
            summary.WarehouseId,
            summary.OwnerId,
            summary.LocationId,
            summary.LocationCode,
            summary.VariantId,
            summary.VariantCode,
            summary.ProductName,
            summary.ExpectedQty,
            summary.ActualQty,
            summary.ReportedBy,
            summary.ReportedByName,
            summary.ReportedAt,
            summary.SourceTaskId,
            summary.SourceTaskNumber,
            summary.VerificationTaskId,
            summary.AdjustmentId,
            summary.Version,
            incident.LotId?.Value,
            lotCode,
            incident.StockStatus?.ToString(),
            incident.RawCode,
            incident.RawPayload,
            incident.Note,
            incident.DeviceId,
            incident.Resolution?.ToString(),
            incident.ResolvedBy,
            incident.ResolvedBy is { } by ? NameOf(lookups.Names, by) : null,
            incident.ResolvedAt,
            incident.ResolutionNote,
            incident.BatchId,
            incident.ParentIncidentId,
            IncidentResolutionRules.Allowed(incident, facts),
            IncidentResolutionRules.CanBeRejected(incident),
            [.. incident.Evidence.OrderBy(value => value.Position)
                .Select(value => new IncidentEvidenceDto(value.FileId, value.Kind.ToString(), value.RawValue))],
            [.. history.Select(value => new IncidentEventDto(
                value.Kind.ToString(),
                value.MemberId,
                value.MemberId is { } member ? NameOf(lookups.Names, member) : null,
                value.Detail,
                value.OccurredAt))]);
    }

    /// <summary>Lo que <c>AcceptIntoStock</c> mira de la línea y de la variante: la misma lectura estrecha que usa <c>resolve</c>, sin
    /// cargar ni rastrear la tarea.</summary>
    internal static async Task<AcceptIntoStockFacts> ReadFactsAsync(
        IWarehouseTaskRepository tasks,
        IVariantProfileRepository profiles,
        Incident incident,
        Guid lineId,
        CancellationToken cancellationToken)
    {
        // TryGetValue y no GetValueOrDefault: una línea sin destino (la de un Count) daría Guid.Empty, que no es null.
        var lines = await tasks.FindLineFactsAsync(incident.TenantId, [lineId], cancellationToken);
        lines.TryGetValue(lineId, out var line);
        var lotTracked = incident.VariantId is { } variant
            && (await profiles.FindAsync(incident.TenantId, variant, cancellationToken))?.Tracking == TrackingPolicy.Lot;
        return new AcceptIntoStockFacts(line?.ToLocationId, line?.Remaining, lotTracked);
    }

    public async Task<IReadOnlyList<IncidentSummaryDto>> ComposeSummariesAsync(
        Guid tenantId, IReadOnlyList<Incident> incidents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incidents);
        if (incidents.Count == 0)
        {
            return [];
        }

        var lookups = await LoadAsync(tenantId, incidents, [], cancellationToken);
        return [.. incidents.Select(incident => ToSummary(incident, lookups))];
    }

    private async Task<Lookups> LoadAsync(
        Guid tenantId, IReadOnlyList<Incident> incidents, IEnumerable<Guid> extraMembers, CancellationToken cancellationToken)
    {
        var locationIds = incidents.Where(value => value.LocationId is not null).Select(value => value.LocationId!.Value).Distinct().ToList();
        var codes = locationIds.Count == 0
            ? new Dictionary<LocationId, string>()
            : await warehouses.FindLocationCodesAsync(tenantId, locationIds, cancellationToken);
        var variantIds = incidents.Where(value => value.VariantId is not null).Select(value => value.VariantId!.Value).Distinct().ToList();
        var variants = variantIds.Count == 0
            ? new Dictionary<Guid, WmsVariantSnapshot>()
            : (await catalog.GetVariantsAsync(tenantId, variantIds, cancellationToken)).ToDictionary(variant => variant.VariantId);
        var taskIds = incidents.Where(value => value.SourceTaskId is not null).Select(value => value.SourceTaskId!.Value).Distinct().ToList();
        var taskNumbers = taskIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await tasks.FindNumbersAsync(tenantId, taskIds, cancellationToken);
        var memberIds = incidents.Select(value => value.ReportedBy).Concat(extraMembers).Where(value => value != Guid.Empty).Distinct().ToList();
        var names = memberIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await members.FindNamesAsync(tenantId, memberIds, cancellationToken);
        return new Lookups(codes, variants, taskNumbers, names);
    }

    private static IncidentSummaryDto ToSummary(Incident incident, Lookups lookups)
    {
        var variant = incident.VariantId is { } variantId ? lookups.Variants.GetValueOrDefault(variantId) : null;
        return new IncidentSummaryDto(
            incident.Id,
            incident.Number,
            incident.Type.ToString(),
            incident.Status.ToString(),
            incident.WarehouseId.Value,
            incident.OwnerId.Value,
            incident.LocationId?.Value,
            incident.LocationId is { } location ? lookups.LocationCodes.GetValueOrDefault(location) ?? string.Empty : null,
            incident.VariantId,
            variant?.Code,
            variant?.ProductName,
            incident.ExpectedQty,
            incident.ActualQty,
            incident.ReportedBy,
            NameOf(lookups.Names, incident.ReportedBy),
            incident.ReportedAt,
            incident.SourceTaskId,
            incident.SourceTaskId is { } taskId ? lookups.TaskNumbers.GetValueOrDefault(taskId) : null,
            incident.VerificationTaskId,
            incident.AdjustmentId,
            incident.Version);
    }

    private static string NameOf(IReadOnlyDictionary<Guid, string> names, Guid memberId) =>
        memberId == Guid.Empty ? SystemName : names.GetValueOrDefault(memberId) ?? UnknownName;
}
