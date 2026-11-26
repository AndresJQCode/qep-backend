using BuildingBlocks.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <param name="ExpectedVersion">La versión que la pantalla cargó; llega por <c>If-Match</c>.</param>
public sealed record ReissueTicketPinCommand(Guid TenantId, Guid TicketId, long ExpectedVersion) : ICommand<SupportTicketDetailDto>;

/// <summary>
/// Spec 2026-11-16 (P-1 a P-6): «Generar PIN nuevo» de un ticket anónimo desde la consola. Un anónimo no tiene correo ni hilo, así
/// que el reenvío público nunca le manda nada; si perdió el PIN o quedó bloqueado, ésta es su única salida. Sólo anónimos: uno con
/// solicitante o cliente (o, aunque no debería pasar, sin fila de acceso) responde <c>422 support.ticket.pin_not_anonymous</c>. Vale
/// en cualquier estado, desbloquea la consulta (P-2) y sube la versión del ticket con el evento <c>PinReissued</c>. El orden es el de
/// <see cref="ExtendTicketHandler"/>; el PIN en claro sólo viaja en la respuesta, con el enlace de la página de consulta, y nunca va a
/// la auditoría, a la traza ni a un log.
/// </summary>
public sealed class ReissueTicketPinHandler(
    IExecutionContext executionContext,
    ITenantDirectory tenantDirectory,
    SupportTicketScope scope,
    ISupportTicketAccessRepository accesses,
    ISupportPinHasher pinHasher,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    ISupportRealtime realtime,
    SupportTicketDtoComposer composer,
    ISupportPublicOptions publicOptions)
    : ICommandHandler<ReissueTicketPinCommand, SupportTicketDetailDto>
{
    // Sólo el nombre del campo: ni el PIN ni su hash entran a la auditoría (P-4).
    private static readonly string[] ChangedFields = ["pinIssuedAt"];

    public async Task<SupportTicketDetailDto> HandleAsync(ReissueTicketPinCommand command, CancellationToken cancellationToken)
    {
        await scope.EnsureAccessAsync(command.TenantId, cancellationToken);

        var (ticket, actor) = await scope.ManageAsync(command.TenantId, command.TicketId, cancellationToken);
        SupportTicketScope.EnsureExternal(ticket);
        SupportTicketScope.EnsureVersion(ticket, command.ExpectedVersion);
        // Todo ticket nace con su clave; si faltara, no hay dónde guardar el PIN nuevo, y la respuesta es la misma que la de un ticket
        // con nombre para que la pantalla no tenga un caso más.
        var access = await accesses.FindAsync(command.TenantId, ticket.Id, cancellationToken)
            ?? throw new SupportDomainException(SupportErrorCodes.PinNotAnonymous, "The ticket has no consult access to reissue.");
        var context = await composer.ReadContextAsync(command.TenantId, cancellationToken);
        var now = context.Now;
        // El dominio rechaza al no anónimo antes de tocar la clave: un 422 no deja ni evento, ni auditoría, ni PIN nuevo.
        ticket.RecordPinReissued(actor, now);
        var slug = await SupportOpeningDelivery.SlugAsync(tenantDirectory, command.TenantId, cancellationToken);

        var pin = SupportPin.Generate();
        access.ReissueByAgent(pinHasher.Hash(pin), now);
        auditPublisher.Publish(
            ticket.TenantId,
            executionContext.SubjectId,
            SupportAuditActions.TicketPinReissued,
            SupportAuditActions.Resources.Ticket,
            ticket.Id.ToString(),
            "success",
            ChangedFields,
            now);
        // El ticket (con su versión) y la clave se guardan juntos: un 412 por la versión deja el PIN viejo intacto.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await realtime.TicketChangedAsync(command.TenantId, ticket.Id.Value, CancellationToken.None);

        var detail = await composer.ComposeDetailAsync(ticket, context, cancellationToken);
        return detail with
        {
            IssuedPin = pin,
            ConsultUrl = SupportOpeningSender.ConsultUrl(publicOptions, slug, ticket.DisplayNumber),
        };
    }
}
