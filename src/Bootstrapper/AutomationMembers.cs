using Modules.Automations.Application;
using Modules.Messaging.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-10-25 D-5: el único punto donde Automations ve quién puede responder conversaciones. Delega en
/// <see cref="IMessagingAssignees.ListAsync"/>: mismos miembros y mismo orden que «Transferir», así que el selector del chatbot y la
/// transferencia manual nunca discrepan.
/// </summary>
internal sealed class AutomationMembers(IMessagingAssignees assignees) : IAutomationMembers
{
    public async Task<IReadOnlyList<AutomationMemberRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        [.. (await assignees.ListAsync(tenantId, cancellationToken)).Select(assignee => new AutomationMemberRef(assignee.MemberId, assignee.DisplayName))];
}
