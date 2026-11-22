using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <summary><c>CustomerId</c> null desvincula el cliente y deja la foto del solicitante.</summary>
public sealed record LinkTicketCustomerRequest(Guid? CustomerId);

/// <param name="ExpectedVersion">La versión que la pantalla cargó; llega por <c>If-Match</c>.</param>
public sealed record LinkTicketCustomerCommand(Guid TenantId, Guid TicketId, Guid? CustomerId, long ExpectedVersion)
    : ICommand<SupportTicketDetailDto>
{
    public static LinkTicketCustomerCommand From(Guid tenantId, Guid ticketId, LinkTicketCustomerRequest request, long expectedVersion) =>
        new(tenantId, ticketId, request.CustomerId, expectedVersion);
}

public sealed class LinkTicketCustomerValidator : AbstractValidator<LinkTicketCustomerCommand>
{
    public LinkTicketCustomerValidator()
    {
        RuleFor(command => command.CustomerId).NotEqual(Guid.Empty);
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

/// <summary>
/// Vincula, cambia o quita el cliente del ticket, en cualquier estado (pre-flight R9). Vincular sigue el plan D14 (módulo
/// <c>customers</c>, cliente activo del tenant, candado del cliente al guardar). Quitarlo exige un solicitante con nombre: si no, el
/// ticket quedaría sin nadie (422 <c>support.ticket.requester_required</c>, lo decide el dominio).
/// </summary>
public sealed class LinkTicketCustomerHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    SupportTicketScope scope,
    ISupportCustomerLookup customers,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    ISupportRealtime realtime,
    SupportTicketDtoComposer composer,
    IValidator<LinkTicketCustomerCommand> validator)
    : ICommandHandler<LinkTicketCustomerCommand, SupportTicketDetailDto>
{
    private static readonly string[] ChangedFields = ["customerId"];

    public async Task<SupportTicketDetailDto> HandleAsync(LinkTicketCustomerCommand command, CancellationToken cancellationToken)
    {
        await scope.EnsureAccessAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var (ticket, actor) = await scope.ManageAsync(command.TenantId, command.TicketId, cancellationToken);
        SupportTicketScope.EnsureVersion(ticket, command.ExpectedVersion);
        // Una tarea no tiene cliente: se rechaza antes de buscar el cliente, que no tiene por qué existir para saberlo.
        SupportTicketScope.EnsureExternal(ticket);
        if (command.CustomerId is { } customerId && customerId != ticket.CustomerId)
        {
            await SupportTicketRules.EnsureCustomerAsync(tenantModules, customers, command.TenantId, customerId, cancellationToken);
        }

        var context = await composer.ReadContextAsync(command.TenantId, cancellationToken);
        var now = context.Now;
        var version = ticket.Version;
        ticket.LinkCustomer(command.CustomerId, actor, now);
        await SupportTicketActions.SaveIfChangedAsync(
            ticket, version, executionContext, auditPublisher, unitOfWork, SupportAuditActions.TicketCustomerChanged, ChangedFields, now,
            cancellationToken);
        if (ticket.Version != version)
        {
            await realtime.TicketChangedAsync(command.TenantId, ticket.Id.Value, CancellationToken.None);
        }

        return await composer.ComposeDetailAsync(ticket, context, cancellationToken);
    }
}
