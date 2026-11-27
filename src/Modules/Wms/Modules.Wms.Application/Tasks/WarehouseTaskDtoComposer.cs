using BuildingBlocks.Application;
using Modules.Wms.Domain.Tasks;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Application;

/// <summary>Arma los DTO de tareas: los códigos de ubicación salen sólo de las ubicaciones que las líneas nombran (sin la bodega
/// entera ni rastreo: la confirmación compone con la tarea y los saldos bloqueados), el producto de Catalog, la unidad base del
/// perfil, el código del lote de los lotes y los nombres de las personas de Tenancy. Las lecturas van a la base: lo que la transacción acaba
/// de crear tiene que estar enviado (<see cref="IWmsUnitOfWork.FlushAsync"/>) antes de componer.</summary>
public sealed class WarehouseTaskDtoComposer(
    IWarehouseRepository warehouses,
    IWmsCatalogReader catalog,
    ILotRepository lots,
    IVariantProfileRepository profiles,
    IWmsMemberDirectory members,
    IClock clock)
{
    private const string UnknownName = "Sin nombre";

    /// <param name="viewerMemberId">Quién va a leer la respuesta: sólo si es quien tiene la tarea recibe el token del reclamo.</param>
    public async Task<TaskDto> ComposeAsync(
        WarehouseTask task, Guid? viewerMemberId, bool withEvents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);

        var codes = await warehouses.FindLocationCodesAsync(
            task.TenantId, [.. task.Lines.SelectMany(LocationsOf).Distinct()], cancellationToken);
        var variantIds = task.Lines.Select(line => line.VariantId).Distinct().ToList();
        var variants = (await catalog.GetVariantsAsync(task.TenantId, variantIds, cancellationToken))
            .ToDictionary(variant => variant.VariantId);
        var baseUoms = (await profiles.ListAsync(task.TenantId, variantIds, cancellationToken))
            .ToDictionary(profile => profile.VariantId, profile => profile.BaseUom.Value);
        var lotIds = task.Lines.Where(line => line.LotId is not null).Select(line => line.LotId!.Value.Value).Distinct().ToList();
        var lotsById = await lots.GetManyAsync(task.TenantId, lotIds, cancellationToken);

        // La historia sigue el orden del servidor (RecordedAt); cada evento conserva su OccurredAt. Los hechos de una transacción
        // comparten fecha y un UUID v7 no ordena dentro del mismo milisegundo: dentro de una fecha manda el tipo, que el enum declara
        // en orden causal.
        IReadOnlyList<WarehouseTaskEvent> history = withEvents
            ? [.. task.Events.OrderBy(value => value.RecordedAt).ThenBy(value => value.Kind).ThenBy(value => value.Id)]
            : [];
        var names = await NamesAsync(
            task.TenantId,
            MembersOf(task).Concat(history.Where(value => value.MemberId is not null).Select(value => value.MemberId!.Value)),
            cancellationToken);
        var summary = ToSummary(task, codes, names, clock.UtcNow);
        var inProgress = task.Status == WarehouseTaskStatus.InProgress;

        return new TaskDto(
            summary.Id,
            summary.Number,
            summary.Type,
            summary.Status,
            summary.WarehouseId,
            summary.OwnerId,
            summary.Priority,
            summary.Urgent,
            summary.EffectivePriority,
            summary.DueAt,
            summary.ReleasedAt,
            summary.AssignedMemberId,
            summary.AssignedMemberName,
            summary.ClaimedByMemberId,
            summary.ClaimedByMemberName,
            summary.LineCount,
            summary.OpenLineCount,
            summary.FirstFromLocationCode,
            summary.LastActivityAt,
            summary.Version,
            task.SourceType.ToString(),
            task.SourceId,
            task.Note,
            inProgress && viewerMemberId is not null && task.ClaimedByMemberId == viewerMemberId ? task.LeaseToken : null,
            inProgress ? task.LeaseExpiresAt : null,
            [.. task.Lines.OrderBy(line => line.Position).Select(line =>
            {
                var variant = variants.GetValueOrDefault(line.VariantId);
                return new TaskLineDto(
                    line.Id,
                    line.Position,
                    line.VariantId,
                    variant?.Code ?? string.Empty,
                    variant?.ProductName ?? string.Empty,
                    line.LotId?.Value,
                    line.LotId is { } lotId ? lotsById.GetValueOrDefault(lotId.Value)?.Code : null,
                    line.StockStatus.ToString(),
                    line.FromLocationId.Value,
                    CodeOf(codes, line.FromLocationId),
                    line.ToLocationId?.Value,
                    line.ToLocationId is { } to ? CodeOf(codes, to) : null,
                    line.QuantityPlanned,
                    line.QuantityConfirmed,
                    line.Remaining,
                    line.Status.ToString(),
                    baseUoms.GetValueOrDefault(line.VariantId) ?? string.Empty,
                    // Sólo lo que el operario contó; lo esperado nunca viaja (DA-39). Un Count muestra planeado y faltante en cero.
                    line.QuantityCounted);
            })],
            [.. history.Select(value => new TaskEventDto(
                value.Kind.ToString(),
                value.MemberId,
                NameOf(names, value.MemberId),
                value.DeviceId,
                value.LineId,
                value.Quantity,
                value.Detail,
                value.OccurredAt))]);
    }

    public async Task<IReadOnlyList<TaskSummaryDto>> ComposeSummariesAsync(
        Guid tenantId, IReadOnlyList<WarehouseTask> tasks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        if (tasks.Count == 0)
        {
            return [];
        }

        var codes = await warehouses.FindLocationCodesAsync(
            tenantId,
            [.. tasks.Select(FirstLine).OfType<WarehouseTaskLine>().Select(line => line.FromLocationId).Distinct()],
            cancellationToken);
        var names = await NamesAsync(tenantId, tasks.SelectMany(MembersOf), cancellationToken);
        var now = clock.UtcNow;

        return
        [
            .. tasks.Select(task => ToSummary(task, codes, names, now)),
        ];
    }

    private static TaskSummaryDto ToSummary(
        WarehouseTask task,
        IReadOnlyDictionary<LocationId, string> codes,
        IReadOnlyDictionary<Guid, string> names,
        DateTimeOffset now)
    {
        var claimer = task.Status == WarehouseTaskStatus.InProgress ? task.ClaimedByMemberId : null;
        var first = FirstLine(task);

        return new TaskSummaryDto(
            task.Id,
            task.Number,
            task.Type.ToString(),
            task.Status.ToString(),
            task.WarehouseId.Value,
            task.OwnerId.Value,
            task.Priority,
            task.Urgent,
            TaskPriority.Score(task.Priority, task.Urgent, task.ReleasedAt, task.DueAt, now),
            task.DueAt,
            task.ReleasedAt,
            task.AssignedMemberId,
            NameOf(names, task.AssignedMemberId),
            claimer,
            NameOf(names, claimer),
            task.Lines.Count,
            task.Lines.Count(line => line.Status == TaskLineStatus.Open),
            first is null ? string.Empty : CodeOf(codes, first.FromLocationId),
            task.LastActivityAt,
            task.Version);
    }

    private static WarehouseTaskLine? FirstLine(WarehouseTask task) => task.Lines.MinBy(line => line.Position);

    private static IEnumerable<LocationId> LocationsOf(WarehouseTaskLine line)
    {
        yield return line.FromLocationId;
        if (line.ToLocationId is { } to)
        {
            yield return to;
        }
    }

    private static IEnumerable<Guid> MembersOf(WarehouseTask task)
    {
        if (task.AssignedMemberId is { } assigned)
        {
            yield return assigned;
        }

        if (task.Status == WarehouseTaskStatus.InProgress && task.ClaimedByMemberId is { } claimer)
        {
            yield return claimer;
        }
    }

    private async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(
        Guid tenantId, IEnumerable<Guid> memberIds, CancellationToken cancellationToken)
    {
        var ids = memberIds.Distinct().ToList();
        return ids.Count == 0
            ? new Dictionary<Guid, string>()
            : await members.FindNamesAsync(tenantId, ids, cancellationToken);
    }

    private static string? NameOf(IReadOnlyDictionary<Guid, string> names, Guid? memberId) =>
        memberId is { } id ? names.GetValueOrDefault(id) ?? UnknownName : null;

    private static string CodeOf(IReadOnlyDictionary<LocationId, string> codes, LocationId id) =>
        codes.GetValueOrDefault(id) ?? string.Empty;
}
