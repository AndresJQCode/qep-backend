using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Las etiquetas del Excel (spec 2026-09-13, A7): las de las tablas de los listados del frontend. Cada
/// prueba recorre todos los valores del enum, no sólo los de la tabla: un estado nuevo sin etiqueta la
/// pone en rojo, en vez de llegar al Excel en inglés sin que nadie se entere.
/// </summary>
public sealed class ExportStatusLabelsTests
{
    // quote-status-badge.tsx
    [Fact]
    public void EveryQuotationStatusHasTheLabelOfTheQuotesTable() =>
        AssertLabels(
            new Dictionary<QuotationStatus, string>
            {
                [QuotationStatus.Draft] = "Borrador",
                [QuotationStatus.Sent] = "Enviada",
                [QuotationStatus.Voided] = "Anulada",
                [QuotationStatus.Expired] = "Vencida",
                [QuotationStatus.Converted] = "Convertida",
            },
            ExportStatusLabels.For);

    // order-list.ts (frontend), las etiquetas de estado del pedido
    [Fact]
    public void EveryOrderStatusHasTheLabelOfTheOrdersTable() =>
        AssertLabels(
            new Dictionary<OrderStatus, string>
            {
                [OrderStatus.Pending] = "Pendiente",
                [OrderStatus.Approved] = "Aprobado",
            },
            ExportStatusLabels.For);

    // order-list.ts (frontend), las etiquetas de estado del pago
    [Fact]
    public void EveryPaymentStatusHasTheLabelOfTheOrdersTable() =>
        AssertLabels(
            new Dictionary<OrderPaymentStatus, string>
            {
                [OrderPaymentStatus.FullPaymentReceived] = "Pago total",
                [OrderPaymentStatus.PartialPaymentReceived] = "Pago parcial",
                [OrderPaymentStatus.PaymentPending] = "Pago pendiente",
            },
            ExportStatusLabels.For);

    private static void AssertLabels<TStatus>(
        IReadOnlyDictionary<TStatus, string> expected, Func<TStatus, string> label)
        where TStatus : struct, Enum =>
        Assert.Equal(
            expected.OrderBy(pair => pair.Key).Select(pair => (pair.Key, pair.Value)),
            Enum.GetValues<TStatus>().Order().Select(status => (status, label(status))));
}
