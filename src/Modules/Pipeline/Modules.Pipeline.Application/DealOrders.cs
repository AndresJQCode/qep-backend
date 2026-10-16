using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pipeline.Domain;

namespace Modules.Pipeline.Application;

public sealed record LinkDealOrderCommand(Guid TenantId, Guid DealId, Guid OrderId, long? Version) : ICommand<DealDetailDto>;

public sealed class LinkDealOrderValidator : AbstractValidator<LinkDealOrderCommand>
{
    public LinkDealOrderValidator() => RuleFor(command => command.Version).NotNull();
}

/// <summary>
/// Spec 2026-10-15 §7: enlazar a mano un pedido sin cotización que llegó con varios negocios abiertos del cliente y quedó
/// suelto (como D27). Tiene que ser del mismo cliente y estar suelto: un pedido que ya ganó un negocio no se mueve, porque
/// moverlo dejaría ganado el otro sin venta. Un pedido es una venta: si el negocio está abierto y el pedido no fue anulado, lo
/// gana, y si el negocio no tiene principal ni monto, toma el del pedido, igual que el aplicador. §6.3: se audita
/// <c>order_linked</c> y, si lo ganó, <c>won</c>, en el mismo SaveChanges.
/// </summary>
public sealed class LinkDealOrderHandler(
    DealCommandSteps steps, IDealOrderRepository orders, IValidator<LinkDealOrderCommand> validator)
    : ICommandHandler<LinkDealOrderCommand, DealDetailDto>
{
    public async Task<DealDetailDto> HandleAsync(LinkDealOrderCommand command, CancellationToken cancellationToken)
    {
        await steps.AuthorizeAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var context = await steps.LoadAsync(command.TenantId, command.DealId, command.Version, cancellationToken);
        var deal = context.Deal;
        var snapshot = await orders.FindAsync(command.TenantId, command.OrderId, cancellationToken)
            ?? throw new PipelineDomainException(PipelineErrorCodes.DealOrderNotFound, "The order has not reached the pipeline.");
        if (snapshot.CustomerId != deal.CustomerId)
        {
            throw new PipelineDomainException(PipelineErrorCodes.DealOrderCustomerMismatch, "The order belongs to another customer.");
        }

        if (snapshot.DealId == deal.Id)
        {
            return await steps.CommitAsync(context, null, cancellationToken);
        }

        if (snapshot.DealId is not null)
        {
            throw new PipelineDomainException(PipelineErrorCodes.DealOrderAlreadyLinked, "The order is already linked to another deal.");
        }

        snapshot.LinkTo(deal.Id, context.Now);
        if (deal.PrimaryQuotationId is null && deal.Amount is null)
        {
            deal.Update(deal.Title, deal.OwnerMemberId, snapshot.Total, snapshot.Currency, deal.ExpectedCloseDate, context.Now);
        }

        if (deal.IsClosed || snapshot.CancelledAt is not null)
        {
            return await steps.CommitAsync(context, PipelineAuditActions.OrderLinked, cancellationToken);
        }

        steps.RecordAudit(context, PipelineAuditActions.OrderLinked, deal.Id);
        deal.Win(context.Pipeline, context.Actor, context.Now);
        return await steps.CommitAsync(context, PipelineAuditActions.DealWon, cancellationToken);
    }
}
