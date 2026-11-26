namespace BuildingBlocks.Application.Realtime;

/// <summary>
/// Spec 2026-11-15 §5.1 (plan N2, N3): the global hub belongs to no module, so each module that uses it registers one authorizer
/// for its topic prefix. The hub rejects a topic without an authorizer. The tenant is always the connection's (from its claims),
/// never one the client sends; the member is the connection's active membership in that tenant.
/// </summary>
public interface IRealtimeTopicAuthorizer
{
    /// <summary>The text of a topic up to its first <c>:</c>, or the whole topic when it has none (e.g. <c>support</c>).</summary>
    string Prefix { get; }

    /// <summary>The message of the <c>HubException</c> the hub throws when this authorizer refuses.</summary>
    string ForbiddenCode { get; }

    Task<bool> CanSubscribeAsync(Guid tenantId, Guid memberId, string topic, CancellationToken cancellationToken);

    /// <summary>Plan N3: announcing presence can need more than reading (Support asks for <c>support.ticket.manage</c>).</summary>
    Task<bool> CanSetPresenceAsync(Guid tenantId, Guid memberId, string topic, CancellationToken cancellationToken);
}
