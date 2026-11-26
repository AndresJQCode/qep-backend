using Microsoft.Extensions.Logging;
using Modules.Siigo.Domain;

namespace Modules.Siigo.Application.Invoicing;

/// <summary>Si el pedido lleva la marca de su factura de Siigo. Lo comparten la recuperación de una tanda y las respuestas con las filas
/// de un pedido (lista, sellar, resolver y anulación), para que avisen igual.</summary>
public static partial class SiigoOrderMark
{
    // Lo más que se espera por la marca: en el apagado, el resultado de un pedido tiene que alcanzar a guardarse.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Si una lectura confirma que el pedido está marcado con ese número. Sólo lee, con 5 s de plazo. Si falla o vence no hay
    /// confirmación: la excepción va a <paramref name="onFailure"/> y responde <c>false</c>. La cancelación de quien llama sí
    /// sube.</summary>
    public static async Task<bool> IsConfirmedAsync(
        ISiigoOrderSource orders,
        Guid tenantId,
        Guid orderId,
        string number,
        Action<Exception> onFailure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(orders);
        ArgumentNullException.ThrowIfNull(onFailure);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(ReadTimeout);
        try
        {
            return await orders.IsMarkedInvoicedAsync(tenantId, orderId, number, limit.Token);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            onFailure(exception);
            return false;
        }
    }

    /// <summary>El <c>orderMarkFailed</c> de una fila que se devuelve al leer: la del pedido, que existe en Siigo y tiene número, cuando
    /// el pedido no confirma la marca. Con el pedido en una tanda sin terminar (<paramref name="queued"/>) no se pregunta ni se avisa: el
    /// worker puede estar marcándolo. La de flete nunca marca el pedido. El log lleva sólo el tipo de la excepción.</summary>
    internal static async Task<bool> IsMissingAsync(
        ISiigoOrderSource orders, SiigoInvoice row, bool queued, ILogger logger, CancellationToken cancellationToken) =>
        !queued
        && row is
        {
            Kind: SiigoInvoiceKind.Order,
            Status: SiigoInvoiceStatus.Created or SiigoInvoiceStatus.Stamped or SiigoInvoiceStatus.StampRejected,
            Number: { } number,
        }
        && !await IsConfirmedAsync(
            orders, row.TenantId, row.OrderId, number, exception => LogReadFailed(logger, exception.GetType().Name), cancellationToken);

    /// <summary>
    /// El <c>canConfirmAnnulment</c> de una fila (spec 2026-11-25, anulación §5): sólo la del pedido, y nunca mientras una tanda sin
    /// terminar lo tenga. Vigente, si toda factura vigente de <paramref name="rows"/> se puede verificar contra Siigo (creada y con id);
    /// ya <c>Annulled</c>, si el pedido sigue marcado (<paramref name="orderUnmarkFailed"/>): ahí la acción reintenta la marca.
    /// </summary>
    internal static bool CanConfirmAnnulment(
        SiigoInvoice row, IReadOnlyList<SiigoInvoice> rows, bool queued, bool orderUnmarkFailed)
    {
        if (queued || row.Kind != SiigoInvoiceKind.Order)
        {
            return false;
        }

        return row.IsLive
            ? rows.Where(value => value.IsLive).All(value => value.IsAnnulmentVerifiable)
            : row.Status == SiigoInvoiceStatus.Annulled && orderUnmarkFailed;
    }

    /// <summary>El <c>orderUnmarkFailed</c> de una fila: la del pedido, <c>Annulled</c> y con número, cuando el pedido todavía lleva la
    /// marca de ese número. Sin confirmación (la lectura falla o vence) responde <c>false</c>.</summary>
    internal static async Task<bool> IsStillMarkedAfterAnnulmentAsync(
        ISiigoOrderSource orders, SiigoInvoice row, ILogger logger, CancellationToken cancellationToken) =>
        row is { Kind: SiigoInvoiceKind.Order, Status: SiigoInvoiceStatus.Annulled, Number: { } number }
        && await IsConfirmedAsync(
            orders,
            row.TenantId,
            row.OrderId,
            number,
            exception => LogUnmarkReadFailed(logger, exception.GetType().Name),
            cancellationToken);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Reading whether the order carries its Siigo invoice failed ({ExceptionType}); the row says the order is not marked.")]
    private static partial void LogReadFailed(ILogger logger, string exceptionType);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Reading whether the order still carries its annulled Siigo invoice failed ({ExceptionType}); the row does not flag it.")]
    private static partial void LogUnmarkReadFailed(ILogger logger, string exceptionType);
}
