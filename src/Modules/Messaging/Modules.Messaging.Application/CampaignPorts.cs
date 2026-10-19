using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-11 §6: las campañas del tenant. Todo método recibe <c>tenantId</c>: el id de otro tenant responde igual que uno
/// inexistente. El agregado se escribe con tracking y se guarda con <see cref="IMessagingUnitOfWork"/> (la auditoría atómica viaja
/// en el mismo <c>DbContext</c>); los destinatarios y los contadores no son del agregado y se leen por <see cref="ICampaignQueries"/>.
/// </summary>
public interface ICampaignRepository
{
    /// <summary>Con tracking.</summary>
    Task<Campaign?> FindAsync(Guid tenantId, Guid campaignId, CancellationToken cancellationToken);

    void Add(Campaign campaign);

    /// <summary>Un borrador no tiene destinatarios, así que no hay nada que borrar aparte.</summary>
    void Remove(Campaign campaign);

    /// <summary>
    /// Guarda la cancelación (con su auditoría) y pasa los destinatarios <c>Pending</c> a <c>Skipped campaign_cancelled</c> en la misma
    /// transacción: o quedan las dos cosas o ninguna. Lo que ya está en vuelo termina (§6.3). Una versión vieja es
    /// <c>concurrency.conflict</c>.
    /// </summary>
    Task SaveCancelledAsync(Campaign campaign, CancellationToken cancellationToken);

    /// <summary>
    /// Guarda el reintento (con su auditoría) y pasa a <c>Pending</c> los destinatarios que
    /// <see cref="CampaignFailureCodes.IsRetriableByHand"/> admite, con sus intentos en cero y el número de reintento de la campaña, en la
    /// misma transacción. Devuelve cuántos reencoló. Una versión vieja es <c>concurrency.conflict</c>.
    /// </summary>
    Task<int> SaveRetriedAsync(Campaign campaign, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>Lo que la lista y el detalle leen de una campaña, con los contadores ya calculados (siempre completos, incluso en cero).</summary>
public sealed record CampaignRow(
    Guid Id,
    Guid ConnectionId,
    string Name,
    string TemplateName,
    string TemplateLanguage,
    string TemplateCategory,
    IReadOnlyDictionary<string, CampaignParameter> Parameters,
    IReadOnlyList<Guid> TagIds,
    IReadOnlyList<Guid> CustomerIds,
    CampaignStatus Status,
    string? FailureReason,
    string? FailureCode,
    DateTimeOffset? ScheduledAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    Guid CreatedByMemberId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version,
    int RetryCount,
    CampaignCountsDto Counts);

/// <summary><c>MessageStatus</c> sale del mensaje que el destinatario tiene atado (<c>message_id</c>); <c>null</c> mientras no hay.</summary>
public sealed record CampaignRecipientRow(
    Guid CustomerId,
    string CustomerName,
    CampaignRecipientStatus Status,
    string? FailureCode,
    string? FailureReason,
    DateTimeOffset? SentAt,
    Guid? ConversationId,
    MessageStatus? MessageStatus,
    int RetryCount);

public interface ICampaignQueries
{
    /// <summary>De la más nueva a la más vieja.</summary>
    Task<(IReadOnlyList<CampaignRow> Items, int Total)> ListAsync(Guid tenantId, int page, int pageSize, CancellationToken cancellationToken);

    Task<CampaignRow?> FindAsync(Guid tenantId, Guid campaignId, CancellationToken cancellationToken);

    /// <summary>Los destinatarios de una campaña del tenant, filtrables por estado, por nombre de cliente.</summary>
    Task<(IReadOnlyList<CampaignRecipientRow> Items, int Total)> ListRecipientsAsync(
        Guid tenantId, Guid campaignId, CampaignRecipientStatus? status, int page, int pageSize, CancellationToken cancellationToken);
}
