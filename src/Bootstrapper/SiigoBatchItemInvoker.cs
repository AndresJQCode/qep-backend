using Bootstrapper.Copilot;
using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Authorization.Application;
using Modules.Siigo.Application;
using Modules.Siigo.Application.Invoicing;
using Modules.Siigo.Domain;

namespace Bootstrapper;

/// <summary>
/// El adaptador de <see cref="ISiigoBatchItemInvoker"/> (spec 2026-11-21): factura un pedido de una tanda <b>como la persona que la pidió</b>,
/// con el mismo <see cref="IssueSiigoInvoiceCommand"/> de siempre y los permisos que esa persona tiene <b>ahora</b>
/// (<see cref="IAuthorizationService.ResolvePermissionsAsync"/>: membresía activa, roles y máscara de módulos). Es el segundo lugar, junto al
/// ejecutor del copiloto, que instala un <see cref="DelegatedExecutionContext"/> (<c>CompositionRootTests</c> lo amarra). Si la persona perdió el
/// permiso, el pedido falla con <see cref="SiigoErrorCodes.InvoiceBatchForbidden"/> y no se llama a Siigo. Todo lo que le pasa al pedido sale
/// como resultado: ni un error del pedido ni de Siigo frena a los demás de la tanda. Hay dos excepciones a «falla», porque que no se
/// pueda hacer el reenvío no dice que la factura no exista: un pedido cuya factura está por confirmar, <c>Unknown</c> o en camino sale por
/// confirmar con ese motivo, y uno cuya factura ya existe en Siigo, sin una de flete <c>Pending</c> por mandar, sale facturado, avisando
/// si el pedido no confirma su marca. Con la de flete todavía <c>Pending</c> sale fallido, también cuando la emisión sí contestó.
/// </summary>
internal sealed partial class SiigoBatchItemInvoker(
    IAuthorizationService authorization, IServiceScopeFactory scopeFactory, ILogger<SiigoBatchItemInvoker> logger) : ISiigoBatchItemInvoker
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Reading whether order {OrderId} carries its Siigo invoice failed; the batch item says the order could not be marked.")]
    private static partial void LogMarkReadFailed(ILogger logger, Exception exception, Guid orderId);

    public async Task<SiigoBatchItemOutcome> InvokeAsync(
        Guid tenantId,
        Guid requestedBy,
        Guid orderId,
        IReadOnlyList<SiigoPaymentChoice>? payments,
        CancellationToken cancellationToken)
    {
        var outcome = await IssueAsync(tenantId, requestedBy, orderId, payments, cancellationToken);
        // Un fallo sin fila es de quien pidió, de la conexión o de la de flete: se mira qué factura tiene el pedido.
        return outcome is SiigoBatchItemOutcome.Failed { InvoiceId: null } failed
            ? await KeepWhatMayExistAsync(tenantId, orderId, failed, cancellationToken)
            : outcome;
    }

    private async Task<SiigoBatchItemOutcome> IssueAsync(
        Guid tenantId,
        Guid requestedBy,
        Guid orderId,
        IReadOnlyList<SiigoPaymentChoice>? payments,
        CancellationToken cancellationToken)
    {
        var permissions = await authorization.ResolvePermissionsAsync(requestedBy, tenantId, cancellationToken);
        if (permissions is null || !permissions.Contains(SiigoPermissions.InvoiceIssue, StringComparer.Ordinal))
        {
            return new SiigoBatchItemOutcome.Failed(SiigoErrorCodes.InvoiceBatchForbidden);
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        // Antes de resolver nada más del scope: todo lo que lea IExecutionContext aquí adentro ve a quien pidió la tanda.
        scope.ServiceProvider.GetRequiredService<DelegatedExecutionScope>()
            .Enter(new DelegatedExecutionContext(requestedBy, tenantId, permissions));
        var dispatcher = scope.ServiceProvider.GetRequiredService<IRequestDispatcher>();
        try
        {
            return Map(await dispatcher.SendAsync(new IssueSiigoInvoiceCommand(tenantId, orderId, payments), cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SiigoInvoiceNotReadyException notReady)
        {
            return new SiigoBatchItemOutcome.Skipped(notReady.Code, [.. notReady.Missing.Select(missing => missing.Code).Distinct()]);
        }
        catch (DomainException domain)
        {
            // Totales que no cuadran, moneda, sin empresa, anulado, ya facturado, sin pagar, sin datos del cliente…: el pedido no está en
            // condiciones. Es del pedido, no de Siigo: no se reintenta y no se llamó a Siigo para crear nada.
            return new SiigoBatchItemOutcome.Skipped(domain.Code, []);
        }
        catch (ResourceNotFoundException notFound)
        {
            return new SiigoBatchItemOutcome.Skipped(notFound.Code, []);
        }
        catch (ValidationException)
        {
            return new SiigoBatchItemOutcome.Failed("validation.failed");
        }
        catch (RequestForbiddenException forbidden)
        {
            return new SiigoBatchItemOutcome.Failed(forbidden.Code);
        }
        catch (ResourceConflictException conflict)
        {
            // La conexión no sirve ahora (pausada, clave rechazada) o dos facturas chocaron.
            return new SiigoBatchItemOutcome.Failed(conflict.Code);
        }
        catch (ServiceUnavailableException unavailable)
        {
            return new SiigoBatchItemOutcome.Failed(unavailable.Code);
        }
        catch (UpstreamServiceException upstream)
        {
            return new SiigoBatchItemOutcome.Failed(upstream.Code);
        }
    }

    // La fila viva del pedido, leída aparte y sin escribir. Facturada y sin flete por mandar: lo que no se pudo fue volver a marcar el
    // pedido, y se avisa como en la recuperación de una tanda. Abierta y en camino, o sin certeza: la decide una persona. Una que nunca
    // salió y nadie está mandando deja el fallo como está.
    private async Task<SiigoBatchItemOutcome> KeepWhatMayExistAsync(
        Guid tenantId, Guid orderId, SiigoBatchItemOutcome.Failed failed, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var rows = await scope.ServiceProvider.GetRequiredService<ISiigoInvoiceRepository>()
            .ListByOrderAsync(tenantId, orderId, cancellationToken);
        var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
        // Vienen de la más vieja a la más nueva.
        var row = rows.LastOrDefault(value => value.Kind == SiigoInvoiceKind.Order && value.IsLive);
        if (row is null)
        {
            return failed;
        }

        if (!row.IsOpen)
        {
            // Con la de flete todavía Pending, el reenvío era para mandarla y no se pudo: «facturado» la dejaría sin mandar a la vista de
            // nadie. Sólo las Pending se reenvían; en cualquier otro estado no cambia lo que pasó con el pedido. El código dice que la del
            // pedido existe, para que nadie la cree otra vez; el motivo del reenvío sale al reintentar desde el pedido.
            if (rows.Any(value => value is { Kind: SiigoInvoiceKind.Freight, Status: SiigoInvoiceStatus.Pending }))
            {
                return failed with { ErrorCode = SiigoErrorCodes.InvoiceFreightPending };
            }

            var marked = row.Number is { } number
                && await SiigoOrderMark.IsConfirmedAsync(
                    scope.ServiceProvider.GetRequiredService<ISiigoOrderSource>(),
                    tenantId,
                    orderId,
                    number,
                    exception => LogMarkReadFailed(logger, exception, orderId),
                    cancellationToken);
            return new SiigoBatchItemOutcome.Invoiced(row.Number ?? string.Empty, row.Id, OrderMarkFailed: !marked);
        }

        return row.IsBeingSent(now) || row.OffersResolution(now)
            ? new SiigoBatchItemOutcome.ToConfirm(row.Id, failed.ErrorCode)
            : failed;
    }

    // La fila del pedido manda: la de flete sola no cambia lo que pasó con el pedido. La salvedad es la de KeepWhatMayExistAsync: con la
    // del pedido en Siigo y la de flete todavía Pending (no salió, o salió y no tuvo respuesta), «facturado» la dejaría sin mandar a la
    // vista de nadie (decidido el 2026-11-25). Reenviar manda sólo la de flete y vuelve a marcar el pedido.
    private static SiigoBatchItemOutcome Map(IReadOnlyList<SiigoInvoiceDto> rows)
    {
        if (rows.FirstOrDefault(row => row.Kind == nameof(SiigoInvoiceKind.Order)) is not { } order)
        {
            return new SiigoBatchItemOutcome.Failed(SiigoErrorCodes.Unavailable);
        }

        if (order.Status is nameof(SiigoInvoiceStatus.Created) or nameof(SiigoInvoiceStatus.Stamped) or nameof(SiigoInvoiceStatus.StampRejected)
            && rows.Any(row => row is { Kind: nameof(SiigoInvoiceKind.Freight), Status: nameof(SiigoInvoiceStatus.Pending) }))
        {
            return new SiigoBatchItemOutcome.Failed(SiigoErrorCodes.InvoiceFreightPending);
        }

        return order.Status switch
        {
            nameof(SiigoInvoiceStatus.Created) or nameof(SiigoInvoiceStatus.Stamped) or nameof(SiigoInvoiceStatus.StampRejected) =>
                new SiigoBatchItemOutcome.Invoiced(order.Number ?? string.Empty, order.Id, order.OrderMarkFailed),
            // Una emisión nunca devuelve como vigente una Annulled: se nombra aquí para que no caiga en «por confirmar».
            nameof(SiigoInvoiceStatus.Rejected) or nameof(SiigoInvoiceStatus.Failed) or nameof(SiigoInvoiceStatus.Annulled) =>
                new SiigoBatchItemOutcome.Failed(order.ErrorCode ?? "siigo.invoice.rejected", order.Id),
            // Pending o Unknown: no se sabe si Siigo la creó. La tanda no la reenvía; una persona la reenvía con los mismos bytes y la misma
            // clave, o la resuelve. La fila trae el motivo (timeout, red, límite, caída).
            _ => new SiigoBatchItemOutcome.ToConfirm(order.Id, order.ErrorCode),
        };
    }
}
