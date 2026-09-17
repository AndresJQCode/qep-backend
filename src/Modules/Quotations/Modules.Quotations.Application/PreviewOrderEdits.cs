using BuildingBlocks.Application;

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
