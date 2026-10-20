using System.Text.Json;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Shopify.Domain;
using Modules.Tenancy.Application;

namespace Modules.Shopify.Application;

/// <summary>Spec 2026-10-15 §7 <c>GET /orders</c> (<c>quotations.order.read</c>). <paramref name="States"/> son nombres de
/// <see cref="ShopifyImportState"/> (sin distinguir mayúsculas); vacío o ausente es <c>Waiting</c>.</summary>
public sealed record ListShopifyOrdersQuery(
    Guid TenantId, IReadOnlyList<string>? States, Guid? ConnectionId, bool? WithWarnings, int? Page, int? PageSize)
    : IQuery<ShopifyOrderPageDto>;

/// <summary>Spec 2026-10-15 §7 <c>GET /orders/summary</c> (<c>quotations.order.read</c>).</summary>
public sealed record GetShopifyOrdersSummaryQuery(Guid TenantId) : IQuery<ShopifyOrdersSummaryDto>;

/// <summary>Los filtros de la bandeja vienen de la query string: un estado que no existe o una página fuera de rango es un 422
/// con <c>errors</c> (<c>state</c>, <c>page</c>, <c>pageSize</c>), no un filtro que se ignora en silencio.</summary>
public sealed class ListShopifyOrdersValidator : AbstractValidator<ListShopifyOrdersQuery>
{
    /// <summary>§7: tope 50 por página; también es el tamaño por defecto.</summary>
    public const int MaxPageSize = 50;

    /// <summary>Como <c>OperatorInput.MaxPage</c>: con este tope, <c>(page - 1) * pageSize</c> no da la vuelta a un OFFSET
    /// negativo con ningún <c>pageSize</c> válido.</summary>
    public const int MaxPage = int.MaxValue / MaxPageSize;

    public ListShopifyOrdersValidator()
    {
        RuleFor(query => query.States)
            .Must(states => states is null || states.All(state => ShopifyOrderFilters.TryParseState(state, out _)))
            .WithMessage($"Each state must be one of {string.Join(", ", Enum.GetNames<ShopifyImportState>())}.")
            .OverridePropertyName("state");
        RuleFor(query => query.Page).InclusiveBetween(1, MaxPage).When(query => query.Page is not null).OverridePropertyName("page");
        RuleFor(query => query.PageSize)
            .InclusiveBetween(1, MaxPageSize)
            .When(query => query.PageSize is not null)
            .OverridePropertyName("pageSize");
    }
}

internal static class ShopifyOrderFilters
{
    public static bool TryParseState(string? value, out ShopifyImportState state)
    {
        state = default;
        var trimmed = value?.Trim();
        return !string.IsNullOrEmpty(trimmed)
            && !trimmed.All(char.IsAsciiDigit)
            && !trimmed.StartsWith('-')
            && Enum.TryParse(trimmed, ignoreCase: true, out state)
            && Enum.IsDefined(state);
    }
}

/// <summary>§7: la página del espejo, los pedidos de Shopify más recientes primero. Sin <c>state</c>, los que esperan.</summary>
public sealed class ListShopifyOrdersHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IShopifyOrderMirrorRepository mirrors,
    ShopifyOrderRowMapper mapper,
    IValidator<ListShopifyOrdersQuery> validator) : IQueryHandler<ListShopifyOrdersQuery, ShopifyOrderPageDto>
{
    public async Task<ShopifyOrderPageDto> HandleAsync(ListShopifyOrdersQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await ShopifyAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, ShopifyPermissions.OrderRead, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var states = (query.States ?? [])
            .Select(state => ShopifyOrderFilters.TryParseState(state, out var parsed) ? parsed : throw new InvalidOperationException("Validated above."))
            .Distinct()
            .ToArray();
        // BFF: sin state, la bandeja muestra lo que espera; pero «con avisos» sin state no filtra por estado, para
        // que la lista cierre con summary.withWarnings (que cuenta en cualquier estado) sin que la pantalla conozca el enum. Un
        // state explícito se respeta siempre.
        var withWarnings = query.WithWarnings == true;
        IReadOnlyCollection<ShopifyImportState> filterStates = states.Length > 0 ? states
            : withWarnings ? []
            : [ShopifyImportState.Waiting];
        var filter = new ShopifyOrderFilter(filterStates, query.ConnectionId, withWarnings);
        var page = query.Page ?? 1;
        var pageSize = query.PageSize ?? ListShopifyOrdersValidator.MaxPageSize;
        var (items, total) = await mirrors.SearchAsync(query.TenantId, filter, page, pageSize, cancellationToken);
        return new ShopifyOrderPageDto(await mapper.MapAsync(query.TenantId, items, cancellationToken), total, page, pageSize);
    }
}

/// <summary>§7: los cuatro contadores de la bandeja en una consulta. «Hoy» es el día del tenant cortado por
/// <see cref="TenantCalendar"/>, como los resúmenes de Reporting: medianoche en su huso, en UTC, con el horario de
/// verano resuelto, y <c>tenancy.tenant.not_found</c> si el tenant no tiene huso (nunca un corte en UTC en silencio).</summary>
public sealed class GetShopifyOrdersSummaryHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IShopifyOrderMirrorRepository mirrors,
    ITenantClock tenantClock) : IQueryHandler<GetShopifyOrdersSummaryQuery, ShopifyOrdersSummaryDto>
{
    public async Task<ShopifyOrdersSummaryDto> HandleAsync(GetShopifyOrdersSummaryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await ShopifyAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, ShopifyPermissions.OrderRead, cancellationToken);

        var calendar = await tenantClock.GetAsync(query.TenantId, cancellationToken);
        var counts = await mirrors.CountAsync(query.TenantId, calendar.StartOfDayUtc(calendar.Today), cancellationToken);
        return new ShopifyOrdersSummaryDto(counts.Waiting, counts.WithWarnings, counts.CancelledAfterInvoice, counts.ImportedToday);
    }
}

/// <summary>Spec 2026-10-15 §6.4 y §7 <c>POST /orders/{id}/retry</c> (<c>quotations.order.manage</c>). Sin cuerpo.</summary>
public sealed record RetryShopifyOrderCommand(Guid TenantId, Guid OrderId) : ICommand<ShopifyOrderRowDto>;

/// <summary>D-S31 y §7 <c>POST /orders/{id}/warnings/review</c> (<c>quotations.order.manage</c>). Sin cuerpo.</summary>
public sealed record ReviewShopifyOrderWarningsCommand(Guid TenantId, Guid OrderId) : ICommand<ShopifyOrderRowDto>;

/// <summary>
/// Las filas del espejo como las dibuja la bandeja (<see cref="ShopifyOrderRowDto"/>). Una página se arma con una lectura de las
/// conexiones del tenant (el nombre de la tienda) y una de los números de pedido (<see cref="IShopifyOrderNumbers"/>), nunca una
/// por fila. El comprador sale del último snapshot (§5.4), no de la ficha de QEP.
/// </summary>
public sealed class ShopifyOrderRowMapper(IShopifyTenantConnections connections, IShopifyOrderNumbers orderNumbers)
{
    public async Task<IReadOnlyList<ShopifyOrderRowDto>> MapAsync(
        Guid tenantId, IReadOnlyList<ShopifyOrderMirror> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return [];
        }

        var names = (await connections.ListAsync(tenantId, cancellationToken))
            .ToDictionary(connection => connection.ConnectionId, connection => connection.Name);
        var orderIds = rows.Where(row => row.QepOrderId is not null).Select(row => row.QepOrderId!.Value).Distinct().ToArray();
        var numbers = orderIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await orderNumbers.FindAsync(tenantId, orderIds, cancellationToken);
        return [.. rows.Select(row => ToDto(row, names.GetValueOrDefault(row.ConnectionId), row.QepOrderId is { } id ? numbers.GetValueOrDefault(id) : null))];
    }

    public async Task<ShopifyOrderRowDto> MapAsync(ShopifyOrderMirror row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        return (await MapAsync(row.TenantId, [row], cancellationToken))[0];
    }

    private static ShopifyOrderRowDto ToDto(ShopifyOrderMirror row, string? storeName, string? orderNumber)
    {
        var buyer = BuyerOf(row.Payload);
        return new ShopifyOrderRowDto(
            row.Id,
            row.ConnectionId,
            row.OrderName,
            storeName,
            row.ShopifyCreatedAt,
            row.FinancialStatus,
            row.TotalPrice,
            row.Currency,
            row.ImportState.ToString(),
            row.WaitReason,
            Json(row.WaitDetail),
            Json(row.Warnings),
            row.WarningsReviewedAt,
            row.Attempts,
            row.LastAttemptAt,
            row.LastError,
            ShopifyLastErrorCodes.Normalize(row.LastError),
            row.QepCustomerId,
            row.QepOrderId,
            orderNumber,
            row.TotalDifference,
            buyer?.Name,
            buyer?.Email);
    }

    private static JsonElement? Json(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    // El payload lo escribió ShopifyOrderIngestion.WritePayload; uno que no se lee (fila vieja, a mano) no tumba la página.
    private static (string? Name, string? Email)? BuyerOf(string payload)
    {
        ShopifyOrderSnapshot snapshot;
        try
        {
            snapshot = ShopifyOrderIngestion.ReadPayload(payload);
        }
        catch (Exception exception) when (exception is JsonException or ShopifyPayloadException or NotSupportedException)
        {
            return null;
        }

        if (snapshot.Buyer is not { } buyer)
        {
            return null;
        }

        var name = string.Join(' ', new[] { buyer.FirstName, buyer.LastName }.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()));
        return (name.Length == 0 ? null : name, string.IsNullOrWhiteSpace(buyer.Email) ? null : buyer.Email.Trim());
    }
}

/// <summary>
/// §6.4: el reintento manual corre la importación en el request por el mismo camino que el webhook y la conciliación
/// (<see cref="ShopifyOrderIngestion.ApplyAsync"/> con el snapshot que guardó el espejo), así que §6.3 y la captura del estado
/// anterior se respetan igual, y la ingesta relee y reintenta si pierde la carrera (<c>concurrency.conflict</c>).
/// <list type="bullet">
/// <item>Fila inexistente en el tenant → 404 <c>shopify.order.not_found</c> (el tenant de la ruta ya se validó: el 404 no confirma
/// nada de otro tenant). Fila que no está <c>Waiting</c> → 422 <c>shopify.order.not_waiting</c>: <c>Imported</c>, <c>Cancelled</c>
/// e <c>Ignored</c> son terminales para la importación.</item>
/// <item>La conexión sale de la ruta del dominio de la tienda (<see cref="IShopifyConnectionDirectory.FindByShopDomainAsync"/>) y
/// tiene que ser la de la fila, del mismo tenant, y operativa; si no (pausada, sin módulo, reenlazada a otro lado) → 422
/// <c>shopify.store.not_operational</c>, el mismo código de «Sincronizar ahora», sin tocar la fila.</item>
/// <item>Un payload del espejo que no se lee (o una cabecera que el dominio no guarda) es un fallo permanente: queda en
/// <c>last_error</c> con el formato del webhook (<c>invalid_payload: …</c>) y la fila vuelve con 200, sin importar.</item>
/// <item>Antes de la ingesta se descarta lo cargado: el importador hace un <c>SaveChanges</c> a mitad de camino (D-S24) que
/// confirmaría cualquier cambio rastreado ajeno al pedido.</item>
/// </list>
/// </summary>
public sealed class RetryShopifyOrderHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IShopifyOrderMirrorRepository mirrors,
    IShopifyStoreRepository stores,
    IShopifyConnectionDirectory connections,
    ShopifyOrderIngestion ingestion,
    IShopifyUnitOfWork unitOfWork,
    ShopifyOrderRowMapper mapper) : ICommandHandler<RetryShopifyOrderCommand, ShopifyOrderRowDto>
{
    public async Task<ShopifyOrderRowDto> HandleAsync(RetryShopifyOrderCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await ShopifyAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, ShopifyPermissions.OrderManage, cancellationToken);

        var row = await ShopifyOrderSupport.FindAsync(mirrors, command.TenantId, command.OrderId, cancellationToken);
        if (row.ImportState != ShopifyImportState.Waiting)
        {
            throw new ShopifyDomainException(
                ShopifyErrorCodes.OrderNotWaiting, $"Only a waiting Shopify order can be retried; this one is {row.ImportState}.");
        }

        var store = await stores.FindAsync(row.TenantId, row.ConnectionId, cancellationToken);
        var connection = store?.ShopDomain is { Length: > 0 } shopDomain
            ? await connections.FindByShopDomainAsync(shopDomain, cancellationToken)
            : await connections.FindAsync(row.TenantId, row.ConnectionId, cancellationToken);
        if (connection is null
            || connection.TenantId != row.TenantId
            || connection.ConnectionId != row.ConnectionId
            || !connection.IsOperational)
        {
            throw new ShopifyDomainException(
                ShopifyErrorCodes.StoreNotOperational, "The Shopify store of this order is not operational; the order cannot be retried now.");
        }

        ShopifyOrderMirror applied;
        try
        {
            var snapshot = ShopifyOrderIngestion.ReadPayload(row.Payload);
            unitOfWork.DiscardPendingChanges();
            applied = await ingestion.ApplyAsync(connection, snapshot, cancellationToken);
        }
        catch (Exception exception) when (exception is ShopifyPayloadException or JsonException or NotSupportedException)
        {
            // Fallo permanente, como en la ingesta y la conciliación: reintentarlo da lo mismo. Queda en last_error con el formato
            // del webhook y la fila vuelve para que la bandeja lo muestre; no es un 500 ni un código que §7.1 no tenga.
            unitOfWork.DiscardPendingChanges();
            applied = await ShopifyOrderSupport.FindAsync(mirrors, command.TenantId, command.OrderId, cancellationToken);
            applied.RecordLastError(DescribePayloadFailure(exception));
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return await mapper.MapAsync(applied, cancellationToken);
    }

    private static string DescribePayloadFailure(Exception exception) => exception switch
    {
        ShopifyPayloadException { Field.Length: > 0 } payload =>
            $"{ShopifyDeliveryResult.InvalidPayloadPrefix}: {payload.Field}: {payload.Message}",
        ShopifyPayloadException payload => $"{ShopifyDeliveryResult.InvalidPayloadPrefix}: {payload.Message}",
        _ => $"{ShopifyDeliveryResult.InvalidPayloadPrefix}: payload: The mirror payload is not a readable snapshot.",
    };
}

/// <summary>
/// D-S31: «Marcar como revisado» llena <c>warnings_reviewed_at</c> y saca la fila del filtro <c>withWarnings</c> y del contador
/// del resumen. Sin avisos → 422 <c>shopify.order.no_warnings</c>; repetirlo conserva la primera revisión. Si pierde la carrera con
/// el worker o la conciliación (<c>concurrency.conflict</c>), relee la fila y vuelve a marcar, hasta tres veces.
/// </summary>
public sealed class ReviewShopifyOrderWarningsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IShopifyOrderMirrorRepository mirrors,
    IShopifyUnitOfWork unitOfWork,
    IClock clock,
    ShopifyOrderRowMapper mapper) : ICommandHandler<ReviewShopifyOrderWarningsCommand, ShopifyOrderRowDto>
{
    private const int MaxAttempts = 3;

    public async Task<ShopifyOrderRowDto> HandleAsync(ReviewShopifyOrderWarningsCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await ShopifyAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, ShopifyPermissions.OrderManage, cancellationToken);

        for (var attempt = 1; ; attempt++)
        {
            var row = await ShopifyOrderSupport.FindAsync(mirrors, command.TenantId, command.OrderId, cancellationToken);
            row.ReviewWarnings(clock.UtcNow);
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return await mapper.MapAsync(row, cancellationToken);
            }
            catch (RequestConcurrencyException exception) when (ShopifyStoreSupport.IsConflict(exception) && attempt < MaxAttempts)
            {
                unitOfWork.DiscardPendingChanges();
            }
        }
    }
}

internal static class ShopifyOrderSupport
{
    public static async Task<ShopifyOrderMirror> FindAsync(
        IShopifyOrderMirrorRepository mirrors, Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        await mirrors.FindByIdAsync(tenantId, id, cancellationToken)
        ?? throw new ResourceNotFoundException(ShopifyErrorCodes.OrderNotFound, "The Shopify order does not exist in this tenant.");
}
