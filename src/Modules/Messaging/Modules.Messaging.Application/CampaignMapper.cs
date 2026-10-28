using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-11 §6.2: arma los DTO de campañas con una llamada a cada directorio por página (nombres de conexión, de quien la
/// creó, y —en el detalle— etiquetas y clientes de la audiencia). Lo que ya no existe no rompe nada: una conexión borrada sale como
/// «Conexión eliminada», una membresía que ya no está como «Miembro eliminado», y una etiqueta o un cliente borrados se omiten.
/// Público sólo para que Infrastructure lo registre.
/// </summary>
public sealed class CampaignMapper(IMessagingConnectionDirectory connections, IMessagingMemberNames memberNames, IMessagingCustomerDirectory customers)
{
    public async Task<IReadOnlyList<CampaignSummaryDto>> SummariesAsync(Guid tenantId, IReadOnlyList<CampaignRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return [];
        }

        var connectionNames = await connections.ListNamesAsync(tenantId, cancellationToken);
        var members = await memberNames.FindAsync(tenantId, [.. rows.Select(row => row.CreatedByMemberId).Distinct()], cancellationToken);
        return [.. rows.Select(row => Summary(row, connectionNames, members))];
    }

    public async Task<CampaignDetailDto> DetailAsync(Guid tenantId, CampaignRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        var summary = (await SummariesAsync(tenantId, [row], cancellationToken))[0];
        var tags = row.TagIds.Count == 0 ? [] : await customers.FindTagsAsync(tenantId, row.TagIds, cancellationToken);
        var refs = row.CustomerIds.Count == 0 ? new Dictionary<Guid, CustomerRefDto>() : await customers.FindRefsAsync(tenantId, row.CustomerIds, cancellationToken);
        var tagsById = tags.ToDictionary(tag => tag.Id);
        return new CampaignDetailDto(
            summary,
            row.Parameters.ToDictionary(pair => pair.Key, pair => new CampaignParameterDto(pair.Value.SourceWire, pair.Value.Value), StringComparer.Ordinal),
            new CampaignAudienceDto(
                [.. row.TagIds.Where(tagsById.ContainsKey).Select(id => new CampaignAudienceTagDto(id, tagsById[id].Name, tagsById[id].Color))],
                [.. row.CustomerIds.Where(refs.ContainsKey).Select(id => new CampaignAudienceCustomerDto(id, refs[id].Name))]));
    }

    public static CampaignRecipientDto Recipient(CampaignRecipientRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new CampaignRecipientDto(
            row.CustomerId, row.CustomerName, row.Status.ToString(), row.FailureReason ?? CampaignFailureReasons.For(row.FailureCode), row.FailureCode,
            row.SentAt, row.ConversationId, row.MessageStatus?.ToString(), row.RetryCount,
            CampaignFailureCodes.IsRetriableByHand(row.Status, row.FailureCode), row.Replied,
            row.Replied && row.MessageStatus != Domain.MessageStatus.Read);
    }

    private static CampaignSummaryDto Summary(
        CampaignRow row, IReadOnlyDictionary<Guid, string> connectionNames, IReadOnlyDictionary<Guid, string> members) =>
        new(
            row.Id,
            row.Name,
            row.Status.ToString(),
            row.FailureReason ?? CampaignFailureReasons.For(row.FailureCode),
            row.FailureCode,
            new CampaignConnectionDto(row.ConnectionId, connectionNames.GetValueOrDefault(row.ConnectionId) ?? ConversationSummaryBuilder.DeletedConnectionName),
            new CampaignTemplateDto(row.TemplateName, row.TemplateLanguage, row.TemplateCategory),
            row.ScheduledAt,
            row.StartedAt,
            row.CompletedAt,
            new MemberRefDto(row.CreatedByMemberId, members.GetValueOrDefault(row.CreatedByMemberId) ?? MessageMapping.DeletedMemberName),
            row.Counts,
            row.CreatedAt,
            row.UpdatedAt,
            row.Version,
            row.RetryCount);
}
