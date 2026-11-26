using BuildingBlocks.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

public sealed record GetTicketQuery(Guid TenantId, Guid TicketId) : IQuery<SupportTicketDetailDto>;

public sealed class GetTicketHandler(
    SupportTicketScope scope,
    SupportTicketDtoComposer composer)
    : IQueryHandler<GetTicketQuery, SupportTicketDetailDto>
{
    public async Task<SupportTicketDetailDto> HandleAsync(GetTicketQuery query, CancellationToken cancellationToken)
    {
        await scope.EnsureAccessAsync(query.TenantId, cancellationToken);

        // Acotado al tenant de la ruta: el ticket de otro tenant no es «no tuyo», es «no existe». Un ticket existente implica la
        // fila de ajustes del tenant, así que no hace falta aprovisionar.
        var ticket = await scope.ViewAsync(query.TenantId, query.TicketId, cancellationToken);

        return await composer.ComposeDetailAsync(ticket, cancellationToken);
    }
}
