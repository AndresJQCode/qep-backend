using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>Spec 2026-11-15 §4: el asesor enlaza el pedido de la tienda a la cuenta verificada de ese correo.</summary>
public sealed record SetOrderBuyerAccountCommand(Guid TenantId, Guid OrderId, long ExpectedVersion, string Email)
    : ICommand<OrderDetailDto>;

/// <summary>Spec 2026-11-15 §4: el asesor quita la cuenta del pedido. Sobre un pedido sin cuenta también vale: significa
/// «no lo enlaces solo».</summary>
public sealed record RemoveOrderBuyerAccountCommand(Guid TenantId, Guid OrderId, long ExpectedVersion)
    : ICommand<OrderDetailDto>;

/// <summary>Las constantes de la cuenta de comprador del pedido.</summary>
public static class OrderBuyerAccountRules
{
    /// <summary>Copia de <c>BuyerEmail.MaxLength</c>: Quotations no ve Buyers. La amarra
    /// <c>BuyerParityTests.TheOrderBuyerAccountEmailLimitMatchesBuyers</c>.</summary>
    public const int EmailMaxLength = 254;

    /// <summary>Cuenta inexistente o sin verificar: un solo código, porque el asesor no necesita distinguirlos.</summary>
    public const string NotFound = "order.buyer_account.not_found";

    public const string AuditAction = "quotation.order.buyer_account_changed";
}

/// <summary>Una cuenta de comprador como la nombra el historial del pedido: <see cref="Email"/> <c>null</c> si ya no existe.</summary>
public sealed record OrderBuyerAccountSnapshot(Guid Id, string? Email);

public sealed class SetOrderBuyerAccountValidator : AbstractValidator<SetOrderBuyerAccountCommand>
{
    public SetOrderBuyerAccountValidator() =>
        RuleFor(command => command.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(OrderBuyerAccountRules.EmailMaxLength);
}

public sealed class SetOrderBuyerAccountHandler(
    IOrderRepository orderRepository,
    IQuotationRepository quotationRepository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationBuyerAccountLookup buyerAccounts,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<SetOrderBuyerAccountCommand> validator)
    : ICommandHandler<SetOrderBuyerAccountCommand, OrderDetailDto>
{
    public async Task<OrderDetailDto> HandleAsync(SetOrderBuyerAccountCommand command, CancellationToken cancellationToken)
    {
        var (order, actor) = await OrderStateAction.LoadAsync(
            orderRepository, membershipDirectory, executionContext, command.TenantId, command.OrderId,
            OrdersPermissions.OrderManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        OrderBuyerAccountChange.EnsureVersion(order, command.ExpectedVersion);
        // El origen antes que la búsqueda: un pedido que no es de la tienda no tiene cuenta, exista o no la del correo.
        OrderBuyerAccountChange.EnsureStoreOrder(order);

        var account = await buyerAccounts.FindVerifiedByEmailAsync(command.Email, cancellationToken)
            ?? throw new QuotationsDomainException(
                OrderBuyerAccountRules.NotFound, "There is no verified buyer account with that email.");

        return await OrderBuyerAccountChange.ApplyAsync(
            order, new OrderBuyerAccountSnapshot(account.Id, account.Email), actor, command.TenantId, orderRepository,
            quotationRepository, unitOfWork, auditPublisher, buyerAccounts, executionContext, clock.UtcNow, cancellationToken);
    }
}

public sealed class RemoveOrderBuyerAccountHandler(
    IOrderRepository orderRepository,
    IQuotationRepository quotationRepository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationBuyerAccountLookup buyerAccounts,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<RemoveOrderBuyerAccountCommand, OrderDetailDto>
{
    public async Task<OrderDetailDto> HandleAsync(RemoveOrderBuyerAccountCommand command, CancellationToken cancellationToken)
    {
        var (order, actor) = await OrderStateAction.LoadAsync(
            orderRepository, membershipDirectory, executionContext, command.TenantId, command.OrderId,
            OrdersPermissions.OrderManage, cancellationToken);
        OrderBuyerAccountChange.EnsureVersion(order, command.ExpectedVersion);

        return await OrderBuyerAccountChange.ApplyAsync(
            order, null, actor, command.TenantId, orderRepository, quotationRepository, unitOfWork, auditPublisher,
            buyerAccounts, executionContext, clock.UtcNow, cancellationToken);
    }
}

/// <summary>Lo que comparten cambiar y quitar la cuenta: el <c>If-Match</c>, el origen y el cambio con su historial y su
/// auditoría.</summary>
internal static class OrderBuyerAccountChange
{
    public static void EnsureVersion(Order order, long expectedVersion)
    {
        if (order.Version != expectedVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The order changed after it was loaded.");
        }
    }

    // Adelanta el error de origen antes de buscar el correo; el respaldo es la guarda de Order.SetBuyerAccountByMember, que es
    // la única que cubre el DELETE.
    public static void EnsureStoreOrder(Order order)
    {
        if (order.Source != OrderSource.Store)
        {
            throw new QuotationsDomainException(
                ExternalOrderErrors.SourceInvalid, "Only a store order has a buyer account.");
        }
    }

    /// <summary>Aplica la decisión del asesor. Si no cambia nada (misma cuenta y la marca ya puesta) responde el pedido
    /// como está, sin historial, sin auditar y sin guardar. El historial nombra los correos, con el asesor como autor; la
    /// auditoría lleva sólo los dos ids, nunca correos. Los dos van en el mismo guardado que el pedido.</summary>
    public static async Task<OrderDetailDto> ApplyAsync(
        Order order,
        OrderBuyerAccountSnapshot? target,
        MemberId actor,
        Guid tenantId,
        IOrderRepository orderRepository,
        IQuotationRepository quotationRepository,
        IQuotationsUnitOfWork unitOfWork,
        IQuotationAuditPublisher auditPublisher,
        IQuotationBuyerAccountLookup buyerAccounts,
        IExecutionContext executionContext,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var previous = order.BuyerAccountId;
        var buyerAccountId = target?.Id;
        if (order.SetBuyerAccountByMember(buyerAccountId, now))
        {
            // Un enlace automático pudo confirmar después de la carga sin subir la versión: la decisión del asesor se escribe
            // siempre, aunque EF vea el mismo valor que leyó.
            orderRepository.ForceBuyerAccountWrite(order);
            var previousAccount = await SnapshotOfAsync(previous, target, buyerAccounts, cancellationToken);
            orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.BuyerAccountChanged, actor,
                OrderChangeSummary.BuyerAccountChanged(previousAccount, target), now));
            auditPublisher.PublishWithDetails(
                tenantId,
                executionContext.SubjectId,
                OrderBuyerAccountRules.AuditAction,
                order.Id.ToString(),
                "success",
                now,
                new Dictionary<string, string?>
                {
                    ["previousBuyerAccountId"] = previous?.ToString(),
                    ["buyerAccountId"] = buyerAccountId?.ToString(),
                });
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return await OrderDetails.LoadAsync(orderRepository, quotationRepository, tenantId, order, cancellationToken);
    }

    // La cuenta anterior con su correo de hoy: la misma que la nueva no se vuelve a buscar, y una borrada sale sin correo.
    private static async Task<OrderBuyerAccountSnapshot?> SnapshotOfAsync(
        Guid? previous,
        OrderBuyerAccountSnapshot? target,
        IQuotationBuyerAccountLookup buyerAccounts,
        CancellationToken cancellationToken)
    {
        if (previous is not { } previousId)
        {
            return null;
        }

        if (target is not null && target.Id == previousId)
        {
            return target;
        }

        var account = await buyerAccounts.FindAsync(previousId, cancellationToken);
        return new OrderBuyerAccountSnapshot(previousId, account?.Email);
    }
}
