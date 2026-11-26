namespace Modules.Siigo.Domain;

/// <summary>El estado <b>efectivo</b> de un pedido de una tanda (spec 2026-11-24 §2): lo que se devuelve al leer. El guardado es lo que
/// pasó en la tanda; un pedido que quedó por confirmar sigue a su factura, que otra tanda puede reenviar o una persona resolver.
/// <see cref="CanResend"/>, <see cref="CanResolve"/> y <see cref="MissingAvailableAt"/> sólo vienen con valor en uno por confirmar.</summary>
public sealed record SiigoBatchItemState(
    SiigoInvoiceBatchItemStatus Status,
    string? ErrorCode,
    string? InvoiceNumber,
    bool CanResend,
    bool CanResolve,
    DateTimeOffset? MissingAvailableAt)
{
    /// <summary>El orden fijo en que se cuentan los estados: viajan todos, incluso en cero.</summary>
    public static IReadOnlyList<SiigoInvoiceBatchItemStatus> CountOrder { get; } =
    [
        SiigoInvoiceBatchItemStatus.Pending,
        SiigoInvoiceBatchItemStatus.Sending,
        SiigoInvoiceBatchItemStatus.Invoiced,
        SiigoInvoiceBatchItemStatus.ToConfirm,
        SiigoInvoiceBatchItemStatus.Failed,
        SiigoInvoiceBatchItemStatus.Skipped,
    ];
}
