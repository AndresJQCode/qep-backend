using System.Globalization;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Tasks;
using Modules.Wms.Domain.Warehousing;
using static Modules.Wms.Application.WmsAuditActions;

namespace Modules.Wms.Application;

/// <summary>Aplanado (S25). <c>OwnerId</c> nulo es «el Self del tenant».</summary>
public sealed record CreateMoveTaskCommand(
    Guid TenantId,
    Guid WarehouseId,
    Guid? OwnerId,
    int? Priority,
    DateTimeOffset? DueAt,
    string? Note,
    bool Publish,
    Guid? AssignToMemberId,
    IReadOnlyList<MoveTaskLineRequest> Lines) : ICommand<TaskDto>
{
    public static CreateMoveTaskCommand From(Guid tenantId, CreateMoveTaskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new(
            tenantId,
            request.WarehouseId,
            request.OwnerId,
            request.Priority,
            request.DueAt,
            request.Note,
            request.Publish ?? true,
            request.AssignToMemberId,
            request.Lines ?? []);
    }
}

/// <summary>El dominio ya valida casi todo, pero con un código o con un <see cref="ArgumentException"/> (la nota larga sería un 500):
/// el validador da el 422 <c>validation.failed</c> con el campo.</summary>
public sealed class CreateMoveTaskValidator : AbstractValidator<CreateMoveTaskCommand>
{
    // La columna es numeric(18,6): una cantidad mayor desbordaría en la base.
    private const decimal QuantityLimit = 1_000_000_000_000m;

    public CreateMoveTaskValidator()
    {
        RuleFor(command => command.Priority)
            .InclusiveBetween(WarehouseTask.MinPriority, WarehouseTask.MaxPriority);
        RuleFor(command => command.Note)
            .Must(note => note is null || note.Trim().Length <= WarehouseTask.NoteMaxLength)
            .WithMessage($"The note has at most {WarehouseTask.NoteMaxLength} characters.");
        RuleFor(command => command.Lines)
            .Must(lines => lines.Count is >= 1 and <= WarehouseTask.MaxLines)
            .WithMessage($"A task has between 1 and {WarehouseTask.MaxLines} lines.");
        RuleForEach(command => command.Lines).NotNull();
        RuleForEach(command => command.Lines).ChildRules(line =>
        {
            line.RuleFor(item => item.Quantity).GreaterThan(0m).LessThan(QuantityLimit);
            line.RuleFor(item => item.StockStatus)
                .Must(status => status is null || Enum.GetNames<StockStatus>().Contains(status, StringComparer.Ordinal))
                .WithMessage($"The status must be one of: {string.Join(", ", Enum.GetNames<StockStatus>())}.");
        });
    }
}

/// <summary>Crea un movimiento interno (spec 2026-11-26 §7.9). Ruta IK. No valida disponibilidad (DA-26): la valida el ledger al
/// confirmar.</summary>
public sealed class CreateMoveTaskHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    WmsCaller caller,
    IWmsProvisioner provisioner,
    IWmsUnitOfWork unitOfWork,
    IOperationReceipts receipts,
    IWmsAuditRecorder auditRecorder,
    IClock clock,
    IOwnerRepository owners,
    IWarehouseRepository warehouses,
    ILotRepository lots,
    IVariantProfileRepository profiles,
    IWmsMemberDirectory members,
    IWarehouseTaskRepository tasks,
    WarehouseTaskDtoComposer composer,
    IValidator<CreateMoveTaskCommand> validator)
    : ICommandHandler<CreateMoveTaskCommand, TaskDto>
{
    public async Task<TaskDto> HandleAsync(CreateMoveTaskCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await authorization.EnsureAsync(
            executionContext, command.TenantId, WmsPermissions.TaskManage, warehouseId: command.WarehouseId, ct: cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var creator = await caller.RequireAsync(command.TenantId, cancellationToken);

        // S5: el Self lo escribe sólo el provisioner, que abre su propia transacción sobre el mismo contexto: va antes de la de aquí.
        if (command.OwnerId is null)
        {
            await provisioner.EnsureAsync(command.TenantId, cancellationToken);
        }

        await EnsureProfilesAsync(command, cancellationToken);

        // La numeración toma un lock de transacción, y el await using deshace todo si guardar lanza OperationReplayedException.
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var ownerId = await ResolveOwnerAsync(command, cancellationToken);
        await authorization.EnsureAsync(
            executionContext, command.TenantId, WmsPermissions.TaskManage, ownerId, command.WarehouseId, cancellationToken);
        var warehouse = await warehouses.GetAsync(command.TenantId, new WarehouseId(command.WarehouseId), cancellationToken)
            ?? throw WmsNotFound.Warehouse(command.WarehouseId);

        await EnsureLotsAsync(command, ownerId, cancellationToken);
        var lines = command.Lines.Select(line => BuildLine(warehouse, line)).ToList();

        if (command.AssignToMemberId is { } assignee
            && !await members.CanPerformAsync(
                command.TenantId, assignee, TaskTypePermissions.For(WarehouseTaskType.Move), cancellationToken))
        {
            throw new WmsDomainException(
                WmsErrorCodes.TaskAssigneeNotEligible, "Esa persona no está activa o no tiene permiso para hacer esta tarea.");
        }

        var now = clock.UtcNow;
        var number = await tasks.NextNumberAsync(command.TenantId, DateOnly.FromDateTime(now.UtcDateTime), cancellationToken);
        var task = WarehouseTask.CreateMove(
            command.TenantId,
            number,
            warehouse.Id,
            new OwnerId(ownerId),
            command.Priority,
            command.DueAt,
            command.Note,
            lines,
            command.AssignToMemberId,
            command.Publish,
            creator,
            now);
        tasks.Add(task);

        var changed = new Dictionary<string, string?>
        {
            ["ownerId"] = ownerId.ToString(),
            ["number"] = task.Number,
            ["lineCount"] = task.Lines.Count.ToString(CultureInfo.InvariantCulture),
        };
        if (task.AssignedMemberId is { } assigned)
        {
            changed["assignedMemberId"] = assigned.ToString();
        }

        auditRecorder.Record(
            command.TenantId, executionContext.SubjectId, WmsActorType.Human, TaskCreated, Resources.Task, task.Id.ToString(), changed, now);

        // La tarea y los perfiles por defecto tienen que estar en la base para que el composer los lea.
        await unitOfWork.FlushAsync(cancellationToken);
        var dto = await composer.ComposeAsync(task, viewerMemberId: null, withEvents: false, cancellationToken);
        receipts.Record(dto);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return dto;
    }

    // P-8: el perfil por defecto nace al primer acceso. De paso frena la variante que Catalog no conoce y la cantidad con más
    // decimales de los que la variante admite, que dejarían una tarea imposible de confirmar.
    private async Task EnsureProfilesAsync(CreateMoveTaskCommand command, CancellationToken cancellationToken)
    {
        foreach (var group in command.Lines.GroupBy(line => line.VariantId))
        {
            var profile = await profiles.GetOrCreateDefaultAsync(command.TenantId, group.Key, cancellationToken);
            if (group.Any(line => decimal.Round(line.Quantity, profile.BaseUomPrecision) != line.Quantity))
            {
                throw new WmsDomainException(
                    WmsErrorCodes.UomPrecisionExceeded,
                    $"La cantidad admite como máximo {profile.BaseUomPrecision} decimales para esta variante.");
            }
        }
    }

    private async Task<Guid> ResolveOwnerAsync(CreateMoveTaskCommand command, CancellationToken cancellationToken)
    {
        if (command.OwnerId is not { } requested)
        {
            return (await owners.GetSelfAsync(command.TenantId, cancellationToken)).Id.Value;
        }

        var owner = await owners.FindAsync(command.TenantId, new OwnerId(requested), cancellationToken)
            ?? throw WmsNotFound.Owner(requested);
        return owner.Id.Value;
    }

    // La FK de la línea al lote no mira el tenant, el dueño ni la variante: la misma regla que aplica el ledger, antes de guardar.
    private Task EnsureLotsAsync(CreateMoveTaskCommand command, Guid ownerId, CancellationToken cancellationToken) =>
        TaskLotRules.EnsureLotsAsync(
            lots,
            command.TenantId,
            ownerId,
            [.. command.Lines.Where(line => line.LotId is not null).Select(line => (line.VariantId, line.LotId!.Value))],
            cancellationToken);

    // Spec §6.11: las dos de la bodega y no virtuales; el destino además admite almacenamiento (el origen puede ser un muelle).
    // Bloqueada o en conteo no se mira aquí: lo rechaza el ledger al confirmar.
    private static WarehouseTaskLine BuildLine(Warehouse warehouse, MoveTaskLineRequest line)
    {
        var from = warehouse.FindLocation(new LocationId(line.FromLocationId));
        var to = warehouse.FindLocation(new LocationId(line.ToLocationId));
        if (from is null || from.IsVirtual || to is null || to.IsVirtual || !to.AllowsStorage)
        {
            throw new WmsDomainException(
                WmsErrorCodes.TaskLocationInvalid,
                "El origen y el destino tienen que ser ubicaciones físicas de la bodega, y el destino admitir almacenamiento.");
        }

        return WarehouseTaskLine.Create(
            line.VariantId,
            line.LotId is { } lot ? new LotId(lot) : null,
            line.StockStatus is null ? StockStatus.Available : Enum.Parse<StockStatus>(line.StockStatus),
            from.Id,
            to.Id,
            line.Quantity);
    }
}
