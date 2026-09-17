using BuildingBlocks.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Guarda de una vez todo lo que «Editar pedido» cambió en su borrador —productos, comprobantes y
/// notas— (spec 2026-09-17, decisiones 1 y 2). <paramref name="ExpectedVersion"/> es el
/// <c>If-Match</c>: el <c>Order.Version</c> que la pantalla cargó (decisión 6).
/// </summary>
public sealed record SaveOrderEditsCommand(
    Guid TenantId,
    Guid QuotationId,
    long ExpectedVersion,
    IReadOnlyList<OrderItemAddition> Items,
    OrderEditProofs Proofs,
    string? Notes) : ICommand<OrderDetailDto>, IOrderEdits;

public sealed class SaveOrderEditsValidator : OrderEditsValidator<SaveOrderEditsCommand>
{
    public SaveOrderEditsValidator()
        : base(requireFileIds: true)
    {
    }
}
