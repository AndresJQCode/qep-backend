using Bootstrapper.Copilot;
using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
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
/// como resultado: ni un error del pedido ni de Siigo frena a los demás de la tanda.
/// </summary>
internal sealed class SiigoBatchItemInvoker(IAuthorizationService authorization, IServiceScopeFactory scopeFactory) : ISiigoBatchItemInvoker
{
    public async Task<SiigoBatchItemOutcome> InvokeAsync(Guid tenantId, Guid requestedBy, Guid orderId, CancellationToken cancellationToken)
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
            return Map(await dispatcher.SendAsync(new IssueSiigoInvoiceCommand(tenantId, orderId, null), cancellationToken));
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

    // La fila del pedido manda: la de flete sola no cambia lo que pasó con el pedido.
    private static SiigoBatchItemOutcome Map(IReadOnlyList<SiigoInvoiceDto> rows)
    {
        var order = rows.FirstOrDefault(row => row.Kind == nameof(SiigoInvoiceKind.Order));
        return order?.Status switch
        {
            nameof(SiigoInvoiceStatus.Created) or nameof(SiigoInvoiceStatus.Stamped) or nameof(SiigoInvoiceStatus.StampRejected) =>
                new SiigoBatchItemOutcome.Invoiced(order.Number ?? string.Empty),
            nameof(SiigoInvoiceStatus.Rejected) or nameof(SiigoInvoiceStatus.Failed) =>
                new SiigoBatchItemOutcome.Failed(order.ErrorCode ?? "siigo.invoice.rejected"),
            // Pending o Unknown: no se sabe si Siigo la creó. La tanda no la reenvía; una persona la reanuda desde el pedido, con los mismos
            // bytes y la misma clave. La fila trae el motivo (timeout, red, límite, caída).
            _ => new SiigoBatchItemOutcome.Failed(order?.ErrorCode ?? SiigoErrorCodes.Unavailable),
        };
    }
}
