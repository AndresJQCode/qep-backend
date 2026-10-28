using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pipeline.Domain;

namespace Modules.Pipeline.Application;

public sealed record LinkDealQuotationCommand(Guid TenantId, Guid DealId, Guid QuotationId, long? Version) : ICommand<DealDetailDto>;

public sealed record SetPrimaryQuotationCommand(Guid TenantId, Guid DealId, Guid QuotationId, long? Version) : ICommand<DealDetailDto>;

public sealed class LinkDealQuotationValidator : AbstractValidator<LinkDealQuotationCommand>
{
    public LinkDealQuotationValidator() => RuleFor(command => command.Version).NotNull();
}

public sealed class SetPrimaryQuotationValidator : AbstractValidator<SetPrimaryQuotationCommand>
{
    public SetPrimaryQuotationValidator() => RuleFor(command => command.Version).NotNull();
}

/// <summary>
/// §4.4 y P11 del plan: enlazar a mano una cotización que llegó por evento. Tiene que ser del mismo cliente; si estaba en otro
/// negocio se mueve, y ése pierde la principal (D27). Si el negocio no tenía principal, ésta pasa a serlo (D17). §6.3: se audita
/// todo lo que pasó, como en el aplicador: <c>quotation_unlinked</c> en el negocio que la pierde, <c>primary_quotation_set</c> si
/// pasó a ser la principal y <c>quotation_linked</c> en éste, en el mismo SaveChanges.
/// </summary>
public sealed class LinkDealQuotationHandler(
    DealCommandSteps steps, IDealQuotationRepository quotations, IDealRepository deals, IValidator<LinkDealQuotationCommand> validator)
    : ICommandHandler<LinkDealQuotationCommand, DealDetailDto>
{
    public async Task<DealDetailDto> HandleAsync(LinkDealQuotationCommand command, CancellationToken cancellationToken)
    {
        await steps.AuthorizeAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var context = await steps.LoadAsync(command.TenantId, command.DealId, command.Version, cancellationToken);
        var deal = context.Deal;
        var snapshot = await quotations.FindAsync(command.TenantId, command.QuotationId, cancellationToken)
            ?? throw new PipelineDomainException(PipelineErrorCodes.DealQuotationNotFound, "The quotation has not reached the pipeline.");
        if (snapshot.CustomerId != deal.CustomerId)
        {
            throw new PipelineDomainException(PipelineErrorCodes.DealQuotationCustomerMismatch, "The quotation belongs to another customer.");
        }

        if (snapshot.DealId == deal.Id)
        {
            return await steps.CommitAsync(context, null, cancellationToken);
        }

        if (snapshot.DealId is { } previousId &&
            await deals.FindAsync(command.TenantId, previousId, cancellationToken) is { } previous)
        {
            if (previous.PrimaryQuotationId == snapshot.QuotationId)
            {
                previous.ClearPrimary(context.Now);
            }

            steps.RecordAudit(context, PipelineAuditActions.QuotationUnlinked, previous.Id);
        }

        snapshot.LinkTo(deal.Id, context.Now);
        if (deal.PrimaryQuotationId is null)
        {
            deal.SetPrimaryQuotation(snapshot.QuotationId, snapshot.Total, snapshot.Currency, context.Now);
            steps.RecordAudit(context, PipelineAuditActions.PrimaryQuotationSet, deal.Id);
        }

        return await steps.CommitAsync(context, PipelineAuditActions.QuotationLinked, cancellationToken);
    }
}

/// <summary>D18: la principal manda el monto y la moneda. Tiene que estar enlazada a este negocio.</summary>
public sealed class SetPrimaryQuotationHandler(
    DealCommandSteps steps, IDealQuotationRepository quotations, IValidator<SetPrimaryQuotationCommand> validator)
    : ICommandHandler<SetPrimaryQuotationCommand, DealDetailDto>
{
    public async Task<DealDetailDto> HandleAsync(SetPrimaryQuotationCommand command, CancellationToken cancellationToken)
    {
        await steps.AuthorizeAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var context = await steps.LoadAsync(command.TenantId, command.DealId, command.Version, cancellationToken);
        var snapshot = await quotations.FindAsync(command.TenantId, command.QuotationId, cancellationToken)
            ?? throw new PipelineDomainException(PipelineErrorCodes.DealQuotationNotFound, "The quotation has not reached the pipeline.");
        if (snapshot.DealId != context.Deal.Id)
        {
            throw new PipelineDomainException(PipelineErrorCodes.DealQuotationNotLinked, "Link the quotation to this deal first.");
        }

        if (context.Deal.PrimaryQuotationId == snapshot.QuotationId)
        {
            return await steps.CommitAsync(context, null, cancellationToken);
        }

        context.Deal.SetPrimaryQuotation(snapshot.QuotationId, snapshot.Total, snapshot.Currency, context.Now);
        return await steps.CommitAsync(context, PipelineAuditActions.PrimaryQuotationSet, cancellationToken);
    }
}
