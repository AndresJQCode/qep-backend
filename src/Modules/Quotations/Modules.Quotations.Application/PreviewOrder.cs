using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>El cálculo previo del pedido manual (spec 2026-10-15 §5.2): el mismo cuerpo que crear,
/// preciado igual, sin número, sin guardar y sin tocar el consecutivo.</summary>
public sealed record PreviewOrderQuery(
    Guid TenantId,
    Guid ClientId,
    string? PaymentMethod,
    string? Notes,
    bool IsRetail,
    int? GlobalScaleFloor,
    QuotationPartiesRequest? Parties,
    QuotationBillingAccountRequest? BillingAccount,
    IReadOnlyList<OrderItemAddition> Items,
    string PaymentStatus,
    IReadOnlyCollection<OrderPaymentProofRequest> PaymentProofs) : IQuery<OrderDetailDto>, IOrderDraft;

public sealed class PreviewOrderValidator : OrderDraftValidator<PreviewOrderQuery>
{
    public PreviewOrderValidator()
        : base(requireBillingAccount: false, requireBillingIdentification: false, requireFileIds: false)
    {
    }
}

public sealed class PreviewOrderHandler(
    OrderDraftBuilder drafts,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<PreviewOrderQuery> validator)
    : IQueryHandler<PreviewOrderQuery, OrderDetailDto>
{
    public async Task<OrderDetailDto> HandleAsync(PreviewOrderQuery query, CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(executionContext, query.TenantId, OrdersPermissions.OrderManage);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var advisorId = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, query.TenantId, cancellationToken);
        var now = clock.UtcNow;
        // Sin archivo todavía (el preview no lo exige): un id cualquiera sirve para sumar el monto.
        var proofs = query.PaymentProofs
            .Select(proof => new OrderPaymentProofInput(Guid.CreateVersion7(), proof.Amount, PaidOn: proof.PaidOn))
            .ToArray();
        var order = await drafts.PreviewAsync(
            new OrderDraftInput(
                query.TenantId, query.ClientId, advisorId, query.PaymentMethod, query.Notes, query.IsRetail,
                query.GlobalScaleFloor, query.Parties, query.BillingAccount, query.Items,
                Enum.Parse<OrderPaymentStatus>(query.PaymentStatus, ignoreCase: true), proofs,
                AllowIncompleteCustomer: false),
            now,
            cancellationToken);

        // Sin id ni número: nada de esto existe todavía, y la pantalla no debe poder enlazarlo.
        return new OrderDetailDto(order.ToDto() with { Id = Guid.Empty, OrderNumber = string.Empty }, null, []);
    }
}
