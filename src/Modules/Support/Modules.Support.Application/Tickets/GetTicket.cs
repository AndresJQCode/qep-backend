using BuildingBlocks.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

public sealed record GetTicketQuery(Guid TenantId, Guid TicketId) : IQuery<SupportTicketDetailDto>;

public sealed class GetTicketHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISupportTicketRepository tickets,
    SupportTicketDtoComposer composer)
    : IQueryHandler<GetTicketQuery, SupportTicketDetailDto>
{
    public async Task<SupportTicketDetailDto> HandleAsync(GetTicketQuery query, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureCanViewAsync(executionContext, tenantModules, query.TenantId, cancellationToken);

        // Acotado al tenant de la ruta: el ticket de otro tenant no es «no tuyo», es «no existe». Un ticket existente implica la
        // fila de ajustes del tenant, así que no hace falta aprovisionar.
        var ticket = await tickets.FindAsync(query.TenantId, new SupportTicketId(query.TicketId), cancellationToken)
            ?? throw SupportNotFound.Ticket(query.TicketId);

        return await composer.ComposeDetailAsync(ticket, cancellationToken);
    }
}
