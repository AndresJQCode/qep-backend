using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// El documento que quedaría si se guardara el borrador de «Editar pedido», sin persistir nada (spec
/// 2026-09-17, decisión 3). Mismo cuerpo que <see cref="SaveOrderEditsCommand"/> salvo archivos.
/// </summary>
public sealed record PreviewOrderEditsQuery(
    Guid TenantId,
    Guid QuotationId,
    IReadOnlyList<OrderItemAddition> Items,
    OrderEditProofs Proofs,
    string? Notes) : IQuery<OrderDetailDto>, IOrderEdits;

public sealed class PreviewOrderEditsValidator : OrderEditsValidator<PreviewOrderEditsQuery>
{
    public PreviewOrderEditsValidator()
        : base(requireFileIds: false)
    {
    }
}

/// <summary>
/// Aplica en memoria los pasos 4, 5 y 7 del guardado sobre agregados leídos **sin rastreo**
/// (decisión 4): mismos métodos de dominio, así que los errores salen con el mismo código. Sin
/// historial, auditoría, outbox ni <c>SaveChangesAsync</c>.
///
/// Los comprobantes nuevos se agregan con un <c>fileId</c> sintético: el dominio no admite
/// <see cref="Guid.Empty"/> y sus montos tienen que contar para el estado de pago. El reemplazo de
/// archivo se ignora (sólo importan los montos). La versión que vuelve es la guardada: la pantalla
/// nunca debe mandar en <c>If-Match</c> una versión que sólo existió en este cálculo.
/// </summary>
public sealed class PreviewOrderEditsHandler(
    IQuotationRepository quotationRepository,
    IOrderRepository orderRepository,
    IQuotationProductPricingLookup pricingLookup,
    IQuotationCustomerLookup customerLookup,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<PreviewOrderEditsQuery> validator)
    : IQueryHandler<PreviewOrderEditsQuery, OrderDetailDto>
{
    public async Task<OrderDetailDto> HandleAsync(
        PreviewOrderEditsQuery query,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, OrdersPermissions.OrderManage);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var quotation = await quotationRepository.FindUntrackedAsync(
            query.TenantId, new QuotationId(query.QuotationId), cancellationToken)
            ?? throw QuotationNotFound.For(query.QuotationId);
        var order = await orderRepository.FindUntrackedAsync(
            query.TenantId, quotation.Id, cancellationToken)
            ?? throw OrderNotFound.For(query.QuotationId);

        if (order.Status != OrderStatus.Pending)
        {
            throw new QuotationsDomainException(
                "order.order.not_pending",
                "An order can only be edited while it is pending.");
        }

        var storedVersion = order.Version;

        var customer = await customerLookup.FindAsync(query.TenantId, quotation.ClientId, cancellationToken);
        if (customer is not null)
        {
            quotation.RefreshCustomerTaxProfile(customer.WithRetention, customer.VatSurplus);
        }

        var updatedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, query.TenantId, cancellationToken);
        var now = clock.UtcNow;

        await OrderItemEdits.ApplyAsync(
            quotation, query.Items, pricingLookup, query.TenantId, updatedBy, now, cancellationToken);

        foreach (var proofId in query.Proofs.RemoveIds)
        {
            order.RemovePaymentProof(new OrderPaymentProofId(proofId), now);
        }

        order.CorrectPaymentProofs(
            query.Proofs.Update
                .Select(update => new OrderPaymentProofAmountUpdate(new OrderPaymentProofId(update.ProofId), update.Amount))
                .ToArray(),
            now);
        order.AttachPaymentProofs(
            query.Proofs.Add
                .Select(addition => new OrderPaymentProofInput(Guid.CreateVersion7(), addition.Amount))
                .ToArray(),
            updatedBy,
            now);
        order.UpdateNotes(query.Notes, now);
        order.RecalculatePaymentStatus(quotation.Total, now);

        return new OrderDetailDto(order.ToDto() with { Version = storedVersion }, quotation.ToDto());
    }
}
