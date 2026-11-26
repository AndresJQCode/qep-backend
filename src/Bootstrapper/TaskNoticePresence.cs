using Bootstrapper.Realtime;
using Modules.Notifications.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-11-22 D-1: the task notice worker asks the app hub's member presence whether the member has QEP open. Notifications
/// never sees the hub; this is the narrow port, mapped one to one.</summary>
internal sealed class TaskNoticePresence(IAppMemberPresence presence) : ITaskNoticePresence
{
    public Task<bool> IsConnectedAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken) =>
        presence.IsConnectedAsync(tenantId, memberId, cancellationToken);
}
