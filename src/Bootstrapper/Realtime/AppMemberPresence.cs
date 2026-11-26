namespace Bootstrapper.Realtime;

/// <summary>
/// Spec 2026-11-22 D-1 and §5.4: whether a member has QEP open, fed by the app hub, which every member with a tenant opens. It decides if
/// a task notice still has to be sent: someone looking at the bell does not need a WhatsApp. Calqued on the inbox presence
/// (<c>MessagingPresence</c>), but keyed by member and tenant instead of by connection, because the question is «is anyone of this member
/// there?».
/// </summary>
internal interface IAppMemberPresence
{
    /// <summary>The hub registered a connection of this member (already in <see cref="AppHubConnections"/>). Never throws, never waits.</summary>
    void Connected(Guid tenantId, Guid memberId);

    /// <summary>The hub removed a connection of this member from <see cref="AppHubConnections"/>; other tabs may remain.</summary>
    void Disconnected(Guid tenantId, Guid memberId);

    Task<bool> IsConnectedAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken);
}

/// <summary>The pod's own answer: the hub registry already has one entry per connection, so there is nothing to keep besides it. It is the
/// presence with <c>Valkey:Areas:Presence</c> off (the member is seen only on the pod that answers) and the fallback of the shared one.</summary>
internal sealed class LocalAppMemberPresence(AppHubConnections connections) : IAppMemberPresence
{
    public void Connected(Guid tenantId, Guid memberId)
    {
    }

    public void Disconnected(Guid tenantId, Guid memberId)
    {
    }

    public Task<bool> IsConnectedAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken) =>
        Task.FromResult(connections.HasMember(tenantId, memberId));
}
