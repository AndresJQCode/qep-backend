using BuildingBlocks.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <param name="Kind">The route segment: <c>privacy-notice</c>, <c>anonymous-notice</c> or <c>terms</c>.</param>
public sealed record GetLegalAcceptanceSnapshotQuery(Guid TenantId, Guid TicketId, string? Kind)
    : IQuery<SupportLegalAcceptanceSnapshotDto>;

/// <summary>
/// What the requester was shown and accepted when filing (spec 2026-11-15 §7, plan L20): the stored snapshot, never today's document.
/// With seeing the ticket it is enough. A ticket of another tenant does not exist (<c>404 support.ticket.not_found</c>), and a kind the
/// ticket did not accept is <c>404 support.legal_acceptance.not_found</c>. Not audited, like the ticket detail.
/// <para>The HTML goes through <see cref="ISupportHtmlSanitizer"/> again on the way out (final review S5), like everything the console
/// paints as HTML: what was stored is already a fixed point, so the bytes do not change, but a row edited by hand cannot reach the
/// console unsanitized. The stored evidence is not touched.</para>
/// </summary>
public sealed class GetLegalAcceptanceSnapshotHandler(
    SupportTicketScope scope,
    ISupportLegalAcceptanceRepository acceptances,
    ISupportHtmlSanitizer sanitizer)
    : IQueryHandler<GetLegalAcceptanceSnapshotQuery, SupportLegalAcceptanceSnapshotDto>
{
    public async Task<SupportLegalAcceptanceSnapshotDto> HandleAsync(
        GetLegalAcceptanceSnapshotQuery query, CancellationToken cancellationToken)
    {
        await scope.EnsureAccessAsync(query.TenantId, cancellationToken);
        var kind = SupportLegalDocuments.ParseKind(query.Kind);
        var ticketId = new SupportTicketId(query.TicketId);
        _ = await scope.ViewAsync(query.TenantId, query.TicketId, cancellationToken);

        var acceptance = await acceptances.FindAsync(query.TenantId, ticketId, kind, cancellationToken)
            ?? throw new ResourceNotFoundException(
                SupportErrorCodes.LegalAcceptanceNotFound,
                $"Ticket '{query.TicketId}' has no acceptance of '{SupportLegalDocumentKinds.ToRoute(kind)}'.");

        return new SupportLegalAcceptanceSnapshotDto(
            acceptance.Kind.ToString(),
            acceptance.DocumentVersion,
            acceptance.AcceptedAt,
            acceptance.VisitorIp,
            sanitizer.Sanitize(acceptance.ContentHtml).Html,
            acceptance.AuthorizationQuestion,
            acceptance.PolicyUrl,
            acceptance.CharacterizationText);
    }
}
