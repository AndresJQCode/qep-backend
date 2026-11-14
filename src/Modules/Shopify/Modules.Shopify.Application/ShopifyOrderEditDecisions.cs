using System.Text.Json;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Audit.Domain;
using Modules.Shopify.Domain;
using Modules.Tenancy.Application;

namespace Modules.Shopify.Application;

/// <summary>Spec 2026-11-14 §6 <c>GET /changes</c> (<c>quotations.order.read</c>): «Cambios por revisar». <paramref name="Statuses"/>
/// son nombres de <see cref="ShopifyOrderEditStatus"/> (sin distinguir mayúsculas); vacío o ausente es <c>Pending</c> y
/// <c>Blocked</c>.</summary>
public sealed record ListShopifyChangesQuery(
    Guid TenantId, IReadOnlyList<string>? Statuses, Guid? ConnectionId, int? Page, int? PageSize) : IQuery<ShopifyOrderChangePageDto>;

/// <summary>Spec 2026-11-14 §6 <c>GET /orders/{id}/changes</c> (<c>quotations.order.read</c>): los cambios de una fila del espejo,
/// los más nuevos primero, en todos los estados.</summary>
public sealed record ListShopifyOrderChangesQuery(Guid TenantId, Guid MirrorOrderId) : IQuery<IReadOnlyList<ShopifyOrderChangeDto>>;

/// <summary>Spec 2026-11-14 §4 <c>POST /changes/{id}/accept</c> (<c>quotations.order.manage</c>).</summary>
public sealed record AcceptShopifyChangeCommand(Guid TenantId, Guid ChangeId) : ICommand<ShopifyOrderChangeDto>;

/// <summary>Spec 2026-11-14 §4 <c>POST /changes/{id}/reject</c> (<c>quotations.order.manage</c>).</summary>
public sealed record RejectShopifyChangeCommand(Guid TenantId, Guid ChangeId) : ICommand<ShopifyOrderChangeDto>;

/// <summary>Spec 2026-11-14 §4 <c>POST /changes/{id}/review</c> (<c>quotations.order.manage</c>): sólo un cambio bloqueado.</summary>
public sealed record ReviewShopifyChangeCommand(Guid TenantId, Guid ChangeId) : ICommand<ShopifyOrderChangeDto>;

/// <summary>Los filtros de «Cambios por revisar» vienen de la query string: un estado que no existe o una página fuera de rango es
/// un 422 con <c>errors</c> (<c>status</c>, <c>page</c>, <c>pageSize</c>), con los topes de la lista de pedidos.</summary>
public sealed class ListShopifyChangesValidator : AbstractValidator<ListShopifyChangesQuery>
{
    public ListShopifyChangesValidator()
    {
        RuleFor(query => query.Statuses)
            .Must(statuses => statuses is null || statuses.All(status => ShopifyOrderEditFilters.TryParseStatus(status, out _)))
            .WithMessage($"Each status must be one of {string.Join(", ", Enum.GetNames<ShopifyOrderEditStatus>())}.")
            .OverridePropertyName("status");
        RuleFor(query => query.Page)
            .InclusiveBetween(1, ListShopifyOrdersValidator.MaxPage)
            .When(query => query.Page is not null)
            .OverridePropertyName("page");
        RuleFor(query => query.PageSize)
            .InclusiveBetween(1, ListShopifyOrdersValidator.MaxPageSize)
            .When(query => query.PageSize is not null)
            .OverridePropertyName("pageSize");
    }
}

internal static class ShopifyOrderEditFilters
{
    public static bool TryParseStatus(string? value, out ShopifyOrderEditStatus status)
    {
        status = default;
        var trimmed = value?.Trim();
        return !string.IsNullOrEmpty(trimmed)
            && !trimmed.All(char.IsAsciiDigit)
            && !trimmed.StartsWith('-')
            && Enum.TryParse(trimmed, ignoreCase: true, out status)
            && Enum.IsDefined(status);
    }
}

/// <summary>
/// Los cambios como los pinta la bandeja (<see cref="ShopifyOrderChangeDto"/>). Una página se arma con una lectura de las filas del
/// espejo (nombre del pedido y moneda), una de las conexiones (nombre de la tienda) y una de los números de pedido de QEP, nunca una
/// por cambio; el nombre de quien decidió, una por persona distinta.
/// </summary>
public sealed class ShopifyOrderChangeMapper(
    IShopifyTenantConnections connections,
    IShopifyOrderNumbers orderNumbers,
    IShopifyMemberDirectory members,
    IShopifyOrderMirrorRepository mirrors)
{
    public async Task<IReadOnlyList<ShopifyOrderChangeDto>> MapAsync(
        Guid tenantId, IReadOnlyList<ShopifyOrderEdit> edits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count == 0)
        {
            return [];
        }

        var rows = (await mirrors.ListByIdsAsync(tenantId, [.. edits.Select(edit => edit.MirrorOrderId).Distinct()], cancellationToken))
            .ToDictionary(row => row.Id);
        var names = (await connections.ListAsync(tenantId, cancellationToken))
            .ToDictionary(connection => connection.ConnectionId, connection => connection.Name);
        var orderIds = edits.Select(edit => edit.QepOrderId).Distinct().ToArray();
        var numbers = await orderNumbers.FindAsync(tenantId, orderIds, cancellationToken);
        var deciders = new Dictionary<Guid, string?>();
        foreach (var decidedBy in edits.Select(edit => edit.DecidedBy).OfType<Guid>().Distinct())
        {
            deciders[decidedBy] = await members.NameAsync(tenantId, decidedBy, cancellationToken);
        }

        return [.. edits.Select(edit =>
        {
            rows.TryGetValue(edit.MirrorOrderId, out var row);
            return new ShopifyOrderChangeDto(
                edit.Id,
                edit.MirrorOrderId,
                edit.ConnectionId,
                names.GetValueOrDefault(edit.ConnectionId),
                row?.OrderName ?? string.Empty,
                edit.QepOrderId,
                numbers.GetValueOrDefault(edit.QepOrderId),
                row?.Currency ?? string.Empty,
                edit.Status.ToString(),
                edit.StatusReason,
                edit.BaseUpdatedAt,
                edit.SnapshotUpdatedAt,
                edit.CreatedAt,
                edit.DecidedAt,
                edit.DecidedBy is { } by ? deciders.GetValueOrDefault(by) : null,
                Json(edit.Groups));
        })];
    }

    public async Task<ShopifyOrderChangeDto> MapAsync(ShopifyOrderEdit edit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return (await MapAsync(edit.TenantId, [edit], cancellationToken))[0];
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
}

/// <summary>Spec 2026-11-14 §6: «Cambios por revisar», los más nuevos primero. Sin <c>status</c>, los pendientes y bloqueados.</summary>
public sealed class ListShopifyChangesHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IShopifyOrderEditRepository edits,
    ShopifyOrderChangeMapper mapper,
    IValidator<ListShopifyChangesQuery> validator) : IQueryHandler<ListShopifyChangesQuery, ShopifyOrderChangePageDto>
{
    public async Task<ShopifyOrderChangePageDto> HandleAsync(ListShopifyChangesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await ShopifyAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, ShopifyPermissions.OrderRead, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var statuses = (query.Statuses ?? [])
            .Select(status => ShopifyOrderEditFilters.TryParseStatus(status, out var parsed)
                ? parsed
                : throw new InvalidOperationException("Validated above."))
            .Distinct()
            .ToArray();
        IReadOnlyCollection<ShopifyOrderEditStatus> filter = statuses.Length > 0
            ? statuses
            : [ShopifyOrderEditStatus.Pending, ShopifyOrderEditStatus.Blocked];
        var page = query.Page ?? 1;
        var pageSize = query.PageSize ?? ListShopifyOrdersValidator.MaxPageSize;
        var (items, total) = await edits.SearchAsync(query.TenantId, filter, query.ConnectionId, page, pageSize, cancellationToken);
        return new ShopifyOrderChangePageDto(await mapper.MapAsync(query.TenantId, items, cancellationToken), total, page, pageSize);
    }
}

/// <summary>Spec 2026-11-14 §6: los cambios de una fila del espejo. Fila inexistente en el tenant → 404 <c>shopify.order.not_found</c>.</summary>
public sealed class ListShopifyOrderChangesHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IShopifyOrderMirrorRepository mirrors,
    IShopifyOrderEditRepository edits,
    ShopifyOrderChangeMapper mapper) : IQueryHandler<ListShopifyOrderChangesQuery, IReadOnlyList<ShopifyOrderChangeDto>>
{
    public async Task<IReadOnlyList<ShopifyOrderChangeDto>> HandleAsync(ListShopifyOrderChangesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await ShopifyAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, ShopifyPermissions.OrderRead, cancellationToken);
        var row = await ShopifyOrderSupport.FindAsync(mirrors, query.TenantId, query.MirrorOrderId, cancellationToken);
        return await mapper.MapAsync(query.TenantId, await edits.ListByMirrorAsync(query.TenantId, row.Id, cancellationToken), cancellationToken);
    }
}

/// <summary>
/// Spec 2026-11-14 §4: lo que una persona decide sobre un cambio de Shopify. Las tres decisiones exigen el permiso de gestión de
/// pedidos y una membresía activa del actor (<c>decided_by</c>, como <c>shopify.stores.updated_by</c>; sin ella, 403). Un cambio que
/// no existe en el tenant es 404 <c>shopify.edit.not_found</c>; uno que ya no está pendiente ni bloqueado, 409
/// <c>shopify.edit.not_open</c>.
/// <list type="bullet">
/// <item><b>Aceptar</b> vuelve a planear contra el pedido de <b>ahora</b> (plan D16): si ya no se puede aplicar, el cambio queda
/// <c>Blocked</c> con su motivo, se guarda y responde 422 <c>shopify.edit.&lt;motivo&gt;</c>. Si el pedido cambió entre el plan y la
/// escritura, Quotations responde <c>concurrency.conflict</c> (412) y el cambio no se toca.</item>
/// <item><b>Rechazar</b> y <b>revisar</b> (sólo un bloqueado, plan D9) adoptan el snapshot como versión aplicada sin tocar el pedido:
/// el siguiente diff no vuelve a proponer lo mismo.</item>
/// </list>
/// </summary>
public sealed class ShopifyChangeDecider(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IMembershipDirectory memberships,
    IShopifyOrderEditRepository edits,
    IShopifyOrderMirrorRepository mirrors,
    IShopifyStoreRepository stores,
    IShopifyOrderWriter orders,
    ShopifyEditPlanner planner,
    ShopifyOrderEdits applier,
    IShopifyAuditRecorder auditRecorder,
    IShopifyUnitOfWork unitOfWork,
    IClock clock,
    ShopifyOrderChangeMapper mapper)
{
    public async Task<ShopifyOrderChangeDto> AcceptAsync(Guid tenantId, Guid changeId, CancellationToken cancellationToken)
    {
        var (edit, decidedBy) = await OpenAsync(tenantId, changeId, cancellationToken);
        var row = await ShopifyOrderSupport.FindAsync(mirrors, tenantId, edit.MirrorOrderId, cancellationToken);
        var store = await stores.FindAsync(tenantId, row.ConnectionId, cancellationToken)
            ?? throw new ShopifyDomainException(ShopifyErrorCodes.StoreNotOperational, "The Shopify store of this change no longer exists.");
        var order = await orders.ReadStateAsync(tenantId, edit.QepOrderId, cancellationToken)
            ?? throw new ResourceNotFoundException(ShopifyErrorCodes.OrderNotFound, "The order of this Shopify change no longer exists.");
        var applied = ShopifyOrderIngestion.ReadPayload(row.AppliedPayload ?? row.Payload);
        var incoming = ShopifyOrderIngestion.ReadPayload(edit.Payload);
        var projection = ShopifyOrderEdits.ProjectionOf(row, store, applied, order);
        var plan = await planner.PlanAsync(store, row, applied, projection, incoming, order, row.QepAttributes, cancellationToken);
        var now = clock.UtcNow;
        if (plan.BlockReason is { } reason)
        {
            throw await BlockAsync(edit, reason, ShopifyOrderEdits.WriteGroups(plan.Groups), now, cancellationToken);
        }

        if (await applier.ApplyAsync(store, row, edit, plan, projection, incoming, decidedBy, cancellationToken) is { } rejected)
        {
            if (string.Equals(rejected, ShopifyOrderEdits.OrderNotEditableCode, StringComparison.Ordinal))
            {
                throw await BlockAsync(edit, ShopifyEditReasons.OrderNotEditable, edit.Groups, now, cancellationToken);
            }

            // Otra regla del pedido (por ejemplo un envío que el dominio no admite): no se aplica y el cambio sigue abierto.
            throw new ShopifyDomainException(rejected, "The order rejected the Shopify change.");
        }

        Audit(tenantId, ShopifyAuditActions.OrderEditAccepted, edit, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await mapper.MapAsync(edit, cancellationToken);
    }

    public Task<ShopifyOrderChangeDto> RejectAsync(Guid tenantId, Guid changeId, CancellationToken cancellationToken) =>
        CloseAsync(tenantId, changeId, ShopifyAuditActions.OrderEditRejected, (edit, by, now) => edit.Reject(by, now), cancellationToken);

    public Task<ShopifyOrderChangeDto> ReviewAsync(Guid tenantId, Guid changeId, CancellationToken cancellationToken) =>
        CloseAsync(tenantId, changeId, ShopifyAuditActions.OrderEditReviewed, (edit, by, now) => edit.Review(by, now), cancellationToken);

    private async Task<ShopifyOrderChangeDto> CloseAsync(
        Guid tenantId,
        Guid changeId,
        string action,
        Action<ShopifyOrderEdit, Guid, DateTimeOffset> close,
        CancellationToken cancellationToken)
    {
        var (edit, decidedBy) = await OpenAsync(tenantId, changeId, cancellationToken);
        var row = await ShopifyOrderSupport.FindAsync(mirrors, tenantId, edit.MirrorOrderId, cancellationToken);
        var now = clock.UtcNow;
        close(edit, decidedBy, now);
        row.AdoptPayload(edit.Payload);
        Audit(tenantId, action, edit, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await mapper.MapAsync(edit, cancellationToken);
    }

    private async Task<(ShopifyOrderEdit Edit, Guid DecidedBy)> OpenAsync(Guid tenantId, Guid changeId, CancellationToken cancellationToken)
    {
        await ShopifyAuthorization.EnsureAsync(executionContext, tenantModules, tenantId, ShopifyPermissions.OrderManage, cancellationToken);
        var decidedBy = await memberships.FindActiveMembershipIdAsync(executionContext.SubjectId, tenantId, cancellationToken)
            ?? throw ShopifyAuthorization.Denied();
        var edit = await edits.FindAsync(tenantId, changeId, cancellationToken)
            ?? throw new ResourceNotFoundException(ShopifyErrorCodes.EditNotFound, "The Shopify change does not exist in this tenant.");
        if (!edit.IsOpen)
        {
            throw new ResourceConflictException(ShopifyErrorCodes.EditNotOpen, "The Shopify change is no longer pending or blocked.");
        }

        return (edit, decidedBy);
    }

    // D16: el bloqueo se guarda aunque la respuesta sea 422, para que la bandeja muestre por qué no se aplicó.
    private async Task<ShopifyDomainException> BlockAsync(
        ShopifyOrderEdit edit, string reason, string groupsJson, DateTimeOffset now, CancellationToken cancellationToken)
    {
        edit.Block(reason, groupsJson, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new ShopifyDomainException($"shopify.edit.{reason}", "The Shopify change cannot be applied to the order now.");
    }

    private void Audit(Guid tenantId, string action, ShopifyOrderEdit edit, DateTimeOffset now) =>
        auditRecorder.Record(
            tenantId,
            executionContext.SubjectId,
            AuditActorType.Human,
            action,
            ShopifyAuditActions.OrderEditResourceType,
            edit.Id.ToString(),
            ChangedFields,
            now);

    // La decisión sólo cambia el estado del cambio (y la versión aplicada de la fila, que no es un campo del usuario).
    private const string ChangedFields = """["status"]""";
}

public sealed class AcceptShopifyChangeHandler(ShopifyChangeDecider decider) : ICommandHandler<AcceptShopifyChangeCommand, ShopifyOrderChangeDto>
{
    public Task<ShopifyOrderChangeDto> HandleAsync(AcceptShopifyChangeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return decider.AcceptAsync(command.TenantId, command.ChangeId, cancellationToken);
    }
}

public sealed class RejectShopifyChangeHandler(ShopifyChangeDecider decider) : ICommandHandler<RejectShopifyChangeCommand, ShopifyOrderChangeDto>
{
    public Task<ShopifyOrderChangeDto> HandleAsync(RejectShopifyChangeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return decider.RejectAsync(command.TenantId, command.ChangeId, cancellationToken);
    }
}

public sealed class ReviewShopifyChangeHandler(ShopifyChangeDecider decider) : ICommandHandler<ReviewShopifyChangeCommand, ShopifyOrderChangeDto>
{
    public Task<ShopifyOrderChangeDto> HandleAsync(ReviewShopifyChangeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return decider.ReviewAsync(command.TenantId, command.ChangeId, cancellationToken);
    }
}
