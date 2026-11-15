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
        var (order, _) = await OrderStateAction.LoadAsync(
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
            order, account.Id, command.TenantId, orderRepository, quotationRepository, unitOfWork, auditPublisher,
            executionContext, clock.UtcNow, cancellationToken);
    }
}

public sealed class RemoveOrderBuyerAccountHandler(
    IOrderRepository orderRepository,
    IQuotationRepository quotationRepository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<RemoveOrderBuyerAccountCommand, OrderDetailDto>
{
    public async Task<OrderDetailDto> HandleAsync(RemoveOrderBuyerAccountCommand command, CancellationToken cancellationToken)
    {
        var (order, _) = await OrderStateAction.LoadAsync(
            orderRepository, membershipDirectory, executionContext, command.TenantId, command.OrderId,
            OrdersPermissions.OrderManage, cancellationToken);
        OrderBuyerAccountChange.EnsureVersion(order, command.ExpectedVersion);

        return await OrderBuyerAccountChange.ApplyAsync(
            order, null, command.TenantId, orderRepository, quotationRepository, unitOfWork, auditPublisher,
            executionContext, clock.UtcNow, cancellationToken);
    }
}

/// <summary>Lo que comparten cambiar y quitar la cuenta: el <c>If-Match</c>, el origen y el cambio con su auditoría.</summary>
internal static class OrderBuyerAccountChange
{
    public static void EnsureVersion(Order order, long expectedVersion)
    {
        if (order.Version != expectedVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The order changed after it was loaded.");
        }
    }

    public static void EnsureStoreOrder(Order order)
    {
        if (order.Source != OrderSource.Store)
        {
            throw new QuotationsDomainException(
                ExternalOrderErrors.SourceInvalid, "Only a store order has a buyer account.");
        }
    }

    /// <summary>Aplica la decisión del asesor. Si no cambia nada (misma cuenta y la marca ya puesta) responde el pedido
    /// como está, sin auditar ni guardar. La auditoría lleva sólo los dos ids, nunca correos.</summary>
    public static async Task<OrderDetailDto> ApplyAsync(
        Order order,
        Guid? buyerAccountId,
        Guid tenantId,
        IOrderRepository orderRepository,
        IQuotationRepository quotationRepository,
        IQuotationsUnitOfWork unitOfWork,
        IQuotationAuditPublisher auditPublisher,
        IExecutionContext executionContext,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var previous = order.BuyerAccountId;
        if (order.SetBuyerAccountByMember(buyerAccountId, now))
        {
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
}
